#import "RemotePanel.h"

@implementation PRPanel {
    NSURL *_page;
    NSString *_token;
    BOOL _pairing;
    BOOL _closing;
    void (^_closed)(void);
}
- (instancetype)initWithConnection:(PRConnection *)connection {
    if ((self = [super init])) {
        _connection = connection;
        _page = [NSBundle.mainBundle URLForResource:@"index" withExtension:@"html" subdirectory:@"Web"];
    }
    return self;
}

- (void)load:(void (^)(NSString *))completion {
    NSURLRequest *probe = [_connection request:@"/api/capabilities" body:nil token:nil timeout:5];
    if (!probe || !_page) { completion(@"无法读取本地页面或连接设置。"); return; }
    [[_connection.session dataTaskWithRequest:probe completionHandler:^(NSData *data, NSURLResponse *response, NSError *error) {
        dispatch_async(dispatch_get_main_queue(), ^{
            if (self->_closing) return;
            if (error || ((NSHTTPURLResponse *)response).statusCode != 401) {
                completion(@"连接失败，请检查证书指纹、地址及对方的被控服务。");
                return;
            }
            [self loadPage:completion];
        });
    }] resume];
}

- (void)loadPage:(void (^)(NSString *))completion {
    WKWebViewConfiguration *config = [WKWebViewConfiguration new];
    config.websiteDataStore = [WKWebsiteDataStore nonPersistentDataStore];
    NSMutableArray *rules = [NSMutableArray array];
    for (NSString *pattern in @[@"^https?://", @"^wss?://", @"^ftp://"]) {
        [rules addObject:@{@"trigger":@{@"url-filter":pattern}, @"action":@{@"type":@"block"}}];
    }
    NSData *data = [NSJSONSerialization dataWithJSONObject:rules options:0 error:NULL];
    NSString *json = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding];
    [[WKContentRuleListStore defaultStore] compileContentRuleListForIdentifier:@"PhoneRemoteOffline" encodedContentRuleList:json
        completionHandler:^(WKContentRuleList *list, NSError *error) {
        if (self->_closing) return;
        NSURL *url = [NSBundle.mainBundle URLForResource:@"bridge" withExtension:@"js"];
        NSString *source = url ? [NSString stringWithContentsOfURL:url encoding:NSUTF8StringEncoding error:NULL] : nil;
        if (!list || !source) { completion(@"无法加载本地控制页面。"); return; }
        [config.userContentController addContentRuleList:list];
        [config.userContentController addUserScript:[[WKUserScript alloc] initWithSource:source
            injectionTime:WKUserScriptInjectionTimeAtDocumentStart forMainFrameOnly:YES]];
        [config.userContentController addScriptMessageHandlerWithReply:self contentWorld:WKContentWorld.pageWorld name:@"remote"];
        self->_view = [[WKWebView alloc] initWithFrame:NSZeroRect configuration:config];
        self->_view.navigationDelegate = self;
        [self->_view loadFileURL:self->_page allowingReadAccessToURL:self->_page.URLByDeletingLastPathComponent];
        completion(nil);
    }];
}

- (void)webView:(WKWebView *)webView decidePolicyForNavigationAction:(WKNavigationAction *)action
    decisionHandler:(void (^)(WKNavigationActionPolicy))decide {
    BOOL allowed = action.targetFrame.isMainFrame && [action.request.URL.URLByStandardizingPath isEqual:_page.URLByStandardizingPath];
    decide(allowed ? WKNavigationActionPolicyAllow : WKNavigationActionPolicyCancel);
}

- (void)userContentController:(WKUserContentController *)controller didReceiveScriptMessage:(WKScriptMessage *)message
                 replyHandler:(void (^)(id, NSString *))reply {
    if (_closing || !message.frameInfo.isMainFrame || message.webView != _view ||
        ![message.frameInfo.request.URL.URLByStandardizingPath isEqual:_page.URLByStandardizingPath] ||
        ![message.body isKindOfClass:NSDictionary.class]) { reply(nil, @"控制页面已关闭。"); return; }
    NSDictionary *args = message.body;
    NSString *path = args[@"path"];
    BOOL pair = [path isEqual:@"/api/pair"];
    if (pair ? (_pairing || _token) : ![_token isEqual:args[@"token"]]) {
        reply(@{@"status":@(pair ? 409 : 401)}, nil);
        return;
    }
    NSString *requestToken = pair ? nil : _token;
    NSURLRequest *request = [_connection request:path body:args[@"body"] token:requestToken timeout:[args[@"timeout"] doubleValue]/1000];
    if (!request) { reply(nil, @"请求无效。"); return; }
    if (pair) _pairing = YES;
    [[_connection.session dataTaskWithRequest:request completionHandler:^(NSData *data, NSURLResponse *response, NSError *error) {
        NSInteger status = ((NSHTTPURLResponse *)response).statusCode;
        NSMutableDictionary *result = [@{@"status":@(status)} mutableCopy];
        NSString *failure = error.localizedDescription;
        NSString *pairedToken = nil;
        if (!failure && status == 200) {
            if (pair || [path isEqual:@"/api/capabilities"]) {
                id payload = data.length <= 16384 ? [NSJSONSerialization JSONObjectWithData:data options:0 error:NULL] : nil;
                if (![payload isKindOfClass:NSDictionary.class]) failure = @"响应格式无效。";
                else if (pair) {
                    id token = payload[@"token"];
                    if ([token isKindOfClass:NSString.class] && [token length] == 64 &&
                        [token rangeOfCharacterFromSet:[[NSCharacterSet characterSetWithCharactersInString:@"abcdefABCDEF0123456789"] invertedSet]].location == NSNotFound) {
                        pairedToken = token;
                        result[@"session"] = @{@"token": token};
                    } else failure = @"配对响应无效。";
                } else result[@"capabilities"] = payload;
            } else if ([path isEqual:@"/api/frame"]) {
                if (![response.MIMEType isEqual:@"image/jpeg"] || data.length > 8 * 1024 * 1024) failure = @"远程画面无效。";
                else result[@"jpeg"] = [data base64EncodedStringWithOptions:0];
            }
        }
        dispatch_async(dispatch_get_main_queue(), ^{
            if (pair) {
                self->_token = [pairedToken copy];
                self->_pairing = NO;
            } else if ([requestToken isEqual:self->_token] && (status == 401 || ([path isEqual:@"/api/disconnect"] && status == 204))) {
                self->_token = nil;
            }
            reply(failure ? nil : result, failure);
            if (pair && self->_closing) [self disconnectAndFinish];
        });
    }] resume];
}

- (void)close:(void (^)(void))completion {
    if (_closing) return;
    _closing = YES;
    _closed = [completion copy];
    [_view.configuration.userContentController removeScriptMessageHandlerForName:@"remote" contentWorld:WKContentWorld.pageWorld];
    [_view stopLoading];
    _view = nil;
    if (!_pairing) [self disconnectAndFinish];
}
- (void)disconnectAndFinish {
    void (^finish)(void) = ^{
        [self->_connection.session invalidateAndCancel];
        self->_token = nil;
        if (self->_closed) self->_closed();
        self->_closed = nil;
    };
    NSURLRequest *request = _token ? [_connection request:@"/api/disconnect" body:@{} token:_token timeout:2] : nil;
    if (!request) { dispatch_async(dispatch_get_main_queue(), finish); return; }
    [[_connection.session dataTaskWithRequest:request completionHandler:^(NSData *data, NSURLResponse *response, NSError *error) {
        dispatch_async(dispatch_get_main_queue(), finish);
    }] resume];
}
@end
