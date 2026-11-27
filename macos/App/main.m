#import <AppKit/AppKit.h>
#import "RemotePanel.h"

@interface PRApplication : NSObject <NSApplicationDelegate>
@end

@implementation PRApplication {
    NSWindow *_window;
    NSTextField *_address, *_fingerprint, *_message;
    NSButton *_connect;
    PRPanel *_panel;
    BOOL _terminating;
}

- (void)applicationDidFinishLaunching:(NSNotification *)note {
    NSMenu *menu = [NSMenu new];
    NSMenuItem *appItem = [NSMenuItem new], *editItem = [NSMenuItem new];
    [menu addItem:appItem];
    [menu addItem:editItem];
    appItem.submenu = [NSMenu new];
    [appItem.submenu addItemWithTitle:@"退出 PhoneRemote" action:@selector(terminate:) keyEquivalent:@"q"];
    editItem.submenu = [[NSMenu alloc] initWithTitle:@"编辑"];
    for (NSArray *entry in @[@[@"剪切",@"cut:",@"x"], @[@"复制",@"copy:",@"c"],
                             @[@"粘贴",@"paste:",@"v"], @[@"全选",@"selectAll:",@"a"]]) {
        [editItem.submenu addItemWithTitle:entry[0] action:NSSelectorFromString(entry[1]) keyEquivalent:entry[2]];
    }
    NSApp.mainMenu = menu;
    _window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0,0,1050,700)
        styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskResizable | NSWindowStyleMaskMiniaturizable
        backing:NSBackingStoreBuffered defer:NO];
    _window.minSize = NSMakeSize(620,480);
    _window.releasedWhenClosed = NO;
    [_window center];
    _address = [NSTextField textFieldWithString:@""];
    _fingerprint = [NSTextField textFieldWithString:@""];
    [self showHome];
    [_window makeKeyAndOrderFront:nil];
    [NSApp activateIgnoringOtherApps:YES];
}
- (NSButton *)button:(NSString *)title action:(SEL)action {
    return [NSButton buttonWithTitle:title target:self action:action];
}
- (NSTextField *)heading:(NSString *)text size:(CGFloat)size {
    NSTextField *label = [NSTextField labelWithString:text];
    label.font = [NSFont systemFontOfSize:size weight:NSFontWeightSemibold];
    return label;
}
- (void)showHome {
    _window.title = @"PhoneRemote";
    NSView *content = [NSView new];
    NSStackView *column = [NSStackView new];
    column.orientation = NSUserInterfaceLayoutOrientationVertical;
    column.alignment = NSLayoutAttributeLeading;
    column.spacing = 10;
    column.translatesAutoresizingMaskIntoConstraints = NO;
    [content addSubview:column];
    [NSLayoutConstraint activateConstraints:@[[column.widthAnchor constraintEqualToConstant:530],
        [column.centerXAnchor constraintEqualToAnchor:content.centerXAnchor], [column.centerYAnchor constraintEqualToAnchor:content.centerYAnchor]]];
    [column addArrangedSubview:[self heading:@"控制另一台电脑" size:26]];
    [column addArrangedSubview:[NSTextField labelWithString:@"在被控的 Mac 或 Windows 电脑上复制连接信息。"]];
    for (NSArray *entry in @[@[@"HTTPS 地址",_address,@"https://192.168.1.10:8443"],
                             @[@"证书 SHA-256 指纹",_fingerprint,@"从被控电脑复制的指纹"]]) {
        [column addArrangedSubview:[NSTextField labelWithString:entry[0]]];
        NSTextField *field = entry[1];
        field.placeholderString = entry[2];
        field.accessibilityLabel = entry[0];
        field.cell.usesSingleLineMode = YES;
        field.font = [NSFont monospacedSystemFontOfSize:13 weight:NSFontWeightRegular];
        [column addArrangedSubview:field];
        [field.widthAnchor constraintEqualToAnchor:column.widthAnchor].active = YES;
    }
    _connect = [self button:@"前往配对" action:@selector(connect:)];
    _connect.keyEquivalent = @"\r";
    [column addArrangedSubview:[NSStackView stackViewWithViews:@[_connect, [self button:@"粘贴连接信息" action:@selector(pasteInfo:)]]]];
    _message = [NSTextField wrappingLabelWithString:@""];
    _message.textColor = NSColor.secondaryLabelColor;
    [column addArrangedSubview:_message];
    [_message.widthAnchor constraintEqualToAnchor:column.widthAnchor].active = YES;
    NSBox *line = [NSBox new];
    line.boxType = NSBoxSeparator;
    [column addArrangedSubview:line];
    [line.widthAnchor constraintEqualToAnchor:column.widthAnchor].active = YES;
    [column setCustomSpacing:17 afterView:line];
    [column addArrangedSubview:[self heading:@"允许其他电脑控制这台 Mac" size:17]];
    [column addArrangedSubview:[NSTextField labelWithString:@"在终端选择监听地址，查看配对码或结束连接。"]];
    [column addArrangedSubview:[self button:@"打开被控服务" action:@selector(startHost:)]];
    _window.contentView = content;
    [_window makeFirstResponder:_address];
}
- (void)pasteInfo:(id)sender {
    NSString *text = [NSPasteboard.generalPasteboard stringForType:NSPasteboardTypeString];
    NSMutableArray *lines = [NSMutableArray array];
    for (NSString *line in [text componentsSeparatedByCharactersInSet:NSCharacterSet.newlineCharacterSet]) {
        NSString *trimmed = [line stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceCharacterSet];
        if (trimmed.length) [lines addObject:trimmed];
    }
    if (lines.count != 2) { _message.stringValue = @"连接信息应有两行，包括 HTTPS 地址和证书指纹。"; return; }
    _address.stringValue = lines[0];
    _fingerprint.stringValue = lines[1];
    _message.stringValue = @"请核对地址和指纹，然后前往配对。";
}
- (void)startHost:(id)sender {
    NSURL *script = [NSBundle.mainBundle URLForResource:@"StartHost" withExtension:@"command"];
    NSURL *terminal = [NSWorkspace.sharedWorkspace URLForApplicationWithBundleIdentifier:@"com.apple.Terminal"];
    if (!script || !terminal) { _message.stringValue = @"找不到被控服务或系统终端。"; return; }
    [NSWorkspace.sharedWorkspace openURLs:@[script] withApplicationAtURL:terminal configuration:[NSWorkspaceOpenConfiguration configuration]
        completionHandler:^(NSRunningApplication *app, NSError *error) {
            if (error) dispatch_async(dispatch_get_main_queue(), ^{ self->_message.stringValue = error.localizedDescription; });
        }];
}
- (void)connect:(id)sender {
    if (_panel) return;
    NSError *error = nil;
    PRConnection *connection = [[PRConnection alloc] initWithAddress:_address.stringValue fingerprint:_fingerprint.stringValue error:&error];
    if (!connection) { _message.stringValue = error.localizedDescription; return; }
    PRPanel *panel = [[PRPanel alloc] initWithConnection:connection];
    _panel = panel;
    _connect.enabled = NO;
    _message.stringValue = @"正在核对证书并连接…";
    [panel load:^(NSString *failure) {
        if (self->_panel != panel || self->_terminating) return;
        if (failure) {
            [panel close:nil];
            self->_panel = nil;
            self->_connect.enabled = YES;
            self->_message.stringValue = failure;
        } else [self showRemote:panel];
    }];
}
- (void)showRemote:(PRPanel *)panel {
    NSString *endpoint = panel.connection.origin.absoluteString;
    _window.title = [@"PhoneRemote · " stringByAppendingString:endpoint];
    NSStackView *top = [NSStackView stackViewWithViews:@[[self button:@"返回连接设置" action:@selector(back:)],
                                                         [NSTextField labelWithString:endpoint]]];
    top.edgeInsets = NSEdgeInsetsMake(7,12,7,12);
    NSView *content = [NSView new], *web = panel.view;
    for (NSView *view in @[top,web]) { view.translatesAutoresizingMaskIntoConstraints = NO; [content addSubview:view]; }
    [NSLayoutConstraint activateConstraints:@[[top.leadingAnchor constraintEqualToAnchor:content.leadingAnchor],
        [top.topAnchor constraintEqualToAnchor:content.topAnchor], [web.topAnchor constraintEqualToAnchor:top.bottomAnchor],
        [web.bottomAnchor constraintEqualToAnchor:content.bottomAnchor], [web.leadingAnchor constraintEqualToAnchor:content.leadingAnchor],
        [web.trailingAnchor constraintEqualToAnchor:content.trailingAnchor]]];
    _window.contentView = content;
}
- (void)back:(id)sender {
    [_panel close:^{
        self->_panel = nil;
        if (self->_terminating) [NSApp replyToApplicationShouldTerminate:YES];
        else [self showHome];
    }];
}
- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)sender { return YES; }
- (NSApplicationTerminateReply)applicationShouldTerminate:(NSApplication *)sender {
    if (!_panel) return NSTerminateNow;
    _terminating = YES;
    [_panel close:^{ [sender replyToApplicationShouldTerminate:YES]; }];
    return NSTerminateLater;
}
@end

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        NSApplication *app = NSApplication.sharedApplication;
        __attribute__((objc_precise_lifetime)) PRApplication *delegate = [PRApplication new];
        app.delegate = delegate;
        [app setActivationPolicy:NSApplicationActivationPolicyRegular];
        [app run];
    }
    return 0;
}
