#import <WebKit/WebKit.h>
#import "Connection.h"

@interface PRPanel : NSObject <WKNavigationDelegate, WKScriptMessageHandlerWithReply>
@property(nonatomic, readonly) WKWebView *view;
@property(nonatomic, readonly) PRConnection *connection;
- (instancetype)initWithConnection:(PRConnection *)connection;
- (void)load:(void (^)(NSString *error))completion;
- (void)close:(void (^)(void))completion;
@end
