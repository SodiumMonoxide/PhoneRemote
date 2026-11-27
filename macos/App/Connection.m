#import "Connection.h"
#import <Security/Security.h>
#import <CommonCrypto/CommonDigest.h>
#import <ifaddrs.h>
#import <arpa/inet.h>

static BOOL remoteHost(NSString *host) {
    struct ifaddrs *interfaces = NULL;
    if (getifaddrs(&interfaces) != 0) return NO;
    BOOL remote = YES;
    for (struct ifaddrs *i = interfaces; i; i = i->ifa_next) {
        if (!i->ifa_addr || i->ifa_addr->sa_family != AF_INET) continue;
        char address[INET_ADDRSTRLEN];
        const struct sockaddr_in *v4 = (const struct sockaddr_in *)i->ifa_addr;
        if (!inet_ntop(AF_INET, &v4->sin_addr, address, sizeof(address)) || [host isEqual:@(address)]) {
            remote = NO;
            break;
        }
    }
    freeifaddrs(interfaces);
    return remote;
}

static id fail(NSError **error, NSString *message) {
    if (error) *error = [NSError errorWithDomain:@"PhoneRemote" code:1 userInfo:@{NSLocalizedDescriptionKey:message}];
    return nil;
}

@implementation PRConnection {
    NSString *_fingerprint;
}
- (instancetype)initWithAddress:(NSString *)address fingerprint:(NSString *)pin error:(NSError **)error {
    self = [super init];
    if (!self) return nil;
    NSCharacterSet *whitespace = NSCharacterSet.whitespaceAndNewlineCharacterSet;
    NSURLComponents *u = [NSURLComponents componentsWithString:[address stringByTrimmingCharactersInSet:whitespace]];
    struct in_addr ip;
    NSString *host = u.host;
    if (![u.scheme.lowercaseString isEqual:@"https"] || !host || u.user || u.password || u.query || u.fragment ||
        (u.path.length && ![u.path isEqual:@"/"]) || inet_pton(AF_INET, host.UTF8String, &ip) != 1) {
        return fail(error, @"请输入被控电脑显示的 HTTPS IPv4 地址。");
    }
    uint32_t v = ntohl(ip.s_addr);
    if ((v >> 24) != 10 && (v >> 20) != 0xac1 && (v >> 16) != 0xc0a8 && (v >> 22) != 0x191) {
        return fail(error, @"请选择局域网或 Tailscale 中另一台电脑的 IPv4 地址。");
    }
    if (!remoteHost(host)) return fail(error, @"请使用另一台电脑的地址。");
    u.scheme = @"https";
    u.path = @"";
    u.port = u.port ?: @8443;
    if (!u.URL || u.port.integerValue < 1 || u.port.integerValue > 65535) return fail(error, @"连接端口无效。");
    _origin = u.URL;
    NSString *hex = [[[pin componentsSeparatedByCharactersInSet:whitespace] componentsJoinedByString:@""]
                     stringByReplacingOccurrencesOfString:@":" withString:@""].lowercaseString;
    NSCharacterSet *invalid = [[NSCharacterSet characterSetWithCharactersInString:@"0123456789abcdef"] invertedSet];
    if (hex.length != 64 || [hex rangeOfCharacterFromSet:invalid].location != NSNotFound)
        return fail(error, @"请粘贴完整的 SHA-256 指纹，共 64 个十六进制字符。");
    _fingerprint = [hex copy];
    NSURLSessionConfiguration *config = NSURLSessionConfiguration.ephemeralSessionConfiguration;
    config.HTTPCookieStorage = nil;
    config.URLCredentialStorage = nil;
    config.URLCache = nil;
    config.connectionProxyDictionary = @{};
    config.TLSMinimumSupportedProtocolVersion = tls_protocol_version_TLSv12;
    config.timeoutIntervalForResource = 15;
    _session = [NSURLSession sessionWithConfiguration:config delegate:self delegateQueue:nil];
    return self;
}

- (NSURLRequest *)request:(NSString *)path body:(NSDictionary *)body token:(NSString *)token timeout:(NSTimeInterval)timeout {
    NSArray *allowed = body ? @[@"/api/input", @"/api/pair", @"/api/disconnect"] : @[@"/api/capabilities", @"/api/frame"];
    if (![allowed containsObject:path] || !remoteHost(_origin.host)) return nil;
    NSMutableURLRequest *r = [NSMutableURLRequest requestWithURL:[NSURL URLWithString:path relativeToURL:_origin].absoluteURL];
    r.timeoutInterval = MAX(1, MIN(15, timeout));
    if (body) {
        if (![NSJSONSerialization isValidJSONObject:body]) return nil;
        NSData *json = [NSJSONSerialization dataWithJSONObject:body options:0 error:nil];
        if (!json || json.length > 4096) return nil;
        r.HTTPMethod = @"POST";
        r.HTTPBody = json;
        [r setValue:@"application/json" forHTTPHeaderField:@"Content-Type"];
        [r setValue:_origin.absoluteString forHTTPHeaderField:@"Origin"];
    }
    if (token) [r setValue:[@"Bearer " stringByAppendingString:token] forHTTPHeaderField:@"Authorization"];
    return r;
}

- (void)URLSession:(NSURLSession *)session task:(NSURLSessionTask *)task willPerformHTTPRedirection:(NSHTTPURLResponse *)response
          newRequest:(NSURLRequest *)request completionHandler:(void (^)(NSURLRequest *))completionHandler {
    completionHandler(nil);
}

static NSDate *validityDate(NSDictionary *properties, CFStringRef oid) {
    id val = properties[(__bridge NSString *)oid][(__bridge NSString *)kSecPropertyKeyValue];
    if ([val isKindOfClass:NSDate.class]) return val;
    if ([val isKindOfClass:NSNumber.class]) return [NSDate dateWithTimeIntervalSinceReferenceDate:[val doubleValue]];
    return nil;
}

- (BOOL)accepts:(NSURLProtectionSpace *)space {
    if (![space.authenticationMethod isEqual:NSURLAuthenticationMethodServerTrust] || ![space.protocol isEqual:@"https"] ||
        ![space.host isEqual:_origin.host] || space.port != _origin.port.integerValue || !space.serverTrust) return NO;
    SecTrustRef trust = space.serverTrust;
    NSArray *chain = CFBridgingRelease(SecTrustCopyCertificateChain(trust));
    if (!chain.count) return NO;
    SecCertificateRef leaf = (__bridge SecCertificateRef)chain.firstObject;
    NSData *der = CFBridgingRelease(SecCertificateCopyData(leaf));
    unsigned char digest[CC_SHA256_DIGEST_LENGTH];
    CC_SHA256(der.bytes, (CC_LONG)der.length, digest);
    NSMutableString *hex = [NSMutableString stringWithCapacity:64];
    for (size_t i = 0; i < sizeof(digest); i++) [hex appendFormat:@"%02x", digest[i]];
    if (![hex isEqual:_fingerprint]) return NO;
    NSArray *oids = @[(__bridge NSString *)kSecOIDX509V1ValidityNotBefore, (__bridge NSString *)kSecOIDX509V1ValidityNotAfter];
    NSDictionary *values = CFBridgingRelease(SecCertificateCopyValues(leaf, (__bridge CFArrayRef)oids, NULL));
    NSDate *start = validityDate(values, kSecOIDX509V1ValidityNotBefore);
    NSDate *end = validityDate(values, kSecOIDX509V1ValidityNotAfter);
    if (!start || !end || start.timeIntervalSinceNow > 0 || end.timeIntervalSinceNow <= 0) return NO;
    SecPolicyRef policy = SecPolicyCreateSSL(true, (__bridge CFStringRef)space.host);
    BOOL valid = SecTrustSetPolicies(trust, policy) == errSecSuccess &&
        SecTrustSetAnchorCertificates(trust, (__bridge CFArrayRef)@[chain.firstObject]) == errSecSuccess &&
        SecTrustSetAnchorCertificatesOnly(trust, true) == errSecSuccess &&
        SecTrustSetNetworkFetchAllowed(trust, false) == errSecSuccess && SecTrustEvaluateWithError(trust, NULL);
    CFRelease(policy);
    return valid;
}

- (void)URLSession:(NSURLSession *)session didReceiveChallenge:(NSURLAuthenticationChallenge *)challenge
    completionHandler:(void (^)(NSURLSessionAuthChallengeDisposition, NSURLCredential *))completionHandler {
    if ([self accepts:challenge.protectionSpace]) {
        completionHandler(NSURLSessionAuthChallengeUseCredential, [NSURLCredential credentialForTrust:challenge.protectionSpace.serverTrust]);
    } else completionHandler(NSURLSessionAuthChallengeCancelAuthenticationChallenge, nil);
}
- (void)URLSession:(NSURLSession *)session task:(NSURLSessionTask *)task didReceiveChallenge:(NSURLAuthenticationChallenge *)challenge
    completionHandler:(void (^)(NSURLSessionAuthChallengeDisposition, NSURLCredential *))completionHandler {
    [self URLSession:session didReceiveChallenge:challenge completionHandler:completionHandler];
}
@end
