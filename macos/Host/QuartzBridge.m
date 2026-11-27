#import <AppKit/AppKit.h>
#import <ApplicationServices/ApplicationServices.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <ImageIO/ImageIO.h>
#include <math.h>

// Called on the user's explicit Start action. Preflight calls below never prompt.
int pr_permissions(void) {
    @autoreleasepool {
        BOOL screen = CGPreflightScreenCaptureAccess() || CGRequestScreenCaptureAccess();
        BOOL input = CGPreflightPostEventAccess() || CGRequestPostEventAccess();
        return (screen ? 1 : 0) | (input ? 2 : 0);
    }
}

static BOOL sessionAvailable(void) {
    NSDictionary *session = CFBridgingRelease(CGSessionCopyCurrentDictionary());
    return [session[(__bridge NSString *)kCGSessionOnConsoleKey] boolValue] &&
           [session[(__bridge NSString *)kCGSessionLoginDoneKey] boolValue] &&
           ![session[@"CGSSessionScreenIsLocked"] boolValue] &&
           !CGDisplayIsAsleep(CGMainDisplayID());
}

int pr_can_input(void) {
    @autoreleasepool { return sessionAvailable() && CGPreflightPostEventAccess(); }
}

// Capture() is serialized by RemoteControlSession. Filter/configuration are reused
// until a display change or capture failure. Late callbacks own their own blocks.
static SCContentFilter *filter;
static SCStreamConfiguration *config;
static CGDirectDisplayID selected;
static CGSize selectedSize;

static BOOL prepareCapture(CGDirectDisplayID main, CGSize size) {
    if (filter && main == selected && CGSizeEqualToSize(size, selectedSize)) return YES;
    filter = nil;
    if (size.width < 1 || size.height < 1) return NO;
    dispatch_semaphore_t ready = dispatch_semaphore_create(0);
    __block SCDisplay *display;
    [SCShareableContent getShareableContentExcludingDesktopWindows:NO onScreenWindowsOnly:YES
        completionHandler:^(SCShareableContent *content, NSError *error) {
            for (SCDisplay *item in content.displays) {
                if (item.displayID == main) { display = item; break; }
            }
            dispatch_semaphore_signal(ready);
        }];
    if (dispatch_semaphore_wait(ready, dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC)) || !display) return NO;
    filter = [[SCContentFilter alloc] initWithDisplay:display excludingWindows:@[]];
    config = [SCStreamConfiguration new];
    double scale = MIN(1.0, MIN(1600.0 / size.width, 1000.0 / size.height));
    config.width = MAX(1, lround(size.width * scale));
    config.height = MAX(1, lround(size.height * scale));
    config.showsCursor = YES;
    selectedSize = size;
    selected = main;
    return YES;
}

void *pr_frame(int *length) {
    @autoreleasepool {
        *length = 0;
        if (!sessionAvailable() || !CGPreflightScreenCaptureAccess()) return NULL;
        CGDirectDisplayID main = CGMainDisplayID();
        if (!prepareCapture(main, CGDisplayBounds(main).size)) return NULL;
        dispatch_semaphore_t ready = dispatch_semaphore_create(0);
        __block NSData *jpeg;
        [SCScreenshotManager captureImageWithFilter:filter configuration:config
            completionHandler:^(CGImageRef image, NSError *error) {
                if (image && !error) {
                    NSMutableData *output = [NSMutableData data];
                    CGImageDestinationRef encoded = CGImageDestinationCreateWithData(
                        (__bridge CFMutableDataRef)output, CFSTR("public.jpeg"), 1, NULL);
                    if (encoded) {
                        NSDictionary *options = @{(__bridge NSString *)kCGImageDestinationLossyCompressionQuality: @0.72};
                        CGImageDestinationAddImage(encoded, image, (__bridge CFDictionaryRef)options);
                        if (CGImageDestinationFinalize(encoded)) jpeg = output;
                        CFRelease(encoded);
                    }
                }
                dispatch_semaphore_signal(ready);
            }];
        if (dispatch_semaphore_wait(ready, dispatch_time(DISPATCH_TIME_NOW, 2 * NSEC_PER_SEC)) ||
            jpeg.length == 0 || jpeg.length > 2 * 1024 * 1024 || !sessionAvailable()) {
            filter = nil;
            return NULL;
        }
        void *copy = malloc(jpeg.length);
        if (!copy) return NULL;
        [jpeg getBytes:copy length:jpeg.length];
        *length = (int)jpeg.length;
        return copy;
    }
}

void pr_free(void *bytes) { free(bytes); }

static CGEventFlags flagsWithLocal(uint64_t remoteFlags) {
    return CGEventSourceFlagsState(kCGEventSourceStateHIDSystemState) | remoteFlags;
}

int pr_key_is_down(uint16_t key) {
    return CGEventSourceKeyState(kCGEventSourceStateHIDSystemState, key);
}

int pr_key(uint16_t key, int down, uint64_t modifier, uint64_t flags, const UniChar *text, int length) {
    CGEventRef event = CGEventCreateKeyboardEvent(NULL, key, down != 0);
    if (!event) return 0;
    if (modifier) CGEventSetType(event, kCGEventFlagsChanged);
    // A previously posted modifier can be visible in the HID state table until
    // this key-up is delivered. Preserve another held side of the same modifier.
    CGEventFlags combined = flagsWithLocal(flags);
    if (!down && modifier && !(flags & modifier)) combined &= ~modifier;
    CGEventSetFlags(event, combined);
    if (length) CGEventKeyboardSetUnicodeString(event, length, text);
    CGEventPost(kCGHIDEventTap, event);
    CFRelease(event);
    return 1;
}

static CGPoint location(double x, double y) {
    if (x >= 0 && y >= 0) {
        CGRect bounds = CGDisplayBounds(CGMainDisplayID());
        return CGPointMake(bounds.origin.x + x * (bounds.size.width - 1),
                           bounds.origin.y + y * (bounds.size.height - 1));
    }
    CGEventRef event = CGEventCreate(NULL);
    if (!event) return CGPointZero;
    CGPoint point = CGEventGetLocation(event);
    CFRelease(event);
    return point;
}

// Use the host's double-click interval for physical down/up sequences too.
static CGPoint lastClickPoint;
static double lastClickTime = -100;
static int leftCount;
int pr_mouse(int type, double x, double y, int explicitCount, uint64_t flags) {
    @autoreleasepool {
        CGPoint point = location(x, y);
        CGMouseButton button = type == kCGEventRightMouseDown || type == kCGEventRightMouseUp ?
            kCGMouseButtonRight : kCGMouseButtonLeft;
        CGEventRef event = CGEventCreateMouseEvent(NULL, type, point, button);
        if (!event) return 0;
        if (type == kCGEventLeftMouseDown) {
            double now = [NSProcessInfo processInfo].systemUptime;
            BOOL consecutive = now - lastClickTime <= NSEvent.doubleClickInterval &&
                fabs(point.x - lastClickPoint.x) <= 4 && fabs(point.y - lastClickPoint.y) <= 4;
            leftCount = explicitCount ? explicitCount : (consecutive ? MIN(leftCount + 1, 3) : 1);
            lastClickTime = now;
            lastClickPoint = point;
        }
        if (type == kCGEventLeftMouseDown || type == kCGEventLeftMouseUp)
            CGEventSetIntegerValueField(event, kCGMouseEventClickState, leftCount);
        if (button == kCGMouseButtonRight)
            CGEventSetIntegerValueField(event, kCGMouseEventClickState, 1);
        CGEventSetFlags(event, flagsWithLocal(flags));
        CGEventPost(kCGHIDEventTap, event);
        CFRelease(event);
        return 1;
    }
}

int pr_scroll(int delta, uint64_t flags) {
    CGEventRef event = CGEventCreateScrollWheelEvent2(NULL, kCGScrollEventUnitLine, 1, delta, 0, 0);
    if (!event) return 0;
    CGEventSetFlags(event, flagsWithLocal(flags));
    CGEventPost(kCGHIDEventTap, event);
    CFRelease(event);
    return 1;
}
