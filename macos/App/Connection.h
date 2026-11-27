#import <Foundation/Foundation.h>

@interface PRConnection : NSObject <NSURLSessionTaskDelegate>
@property(nonatomic, readonly) NSURL *origin;
@property(nonatomic, readonly) NSURLSession *session;
- (instancetype)initWithAddress:(NSString *)address fingerprint:(NSString *)pin error:(NSError **)error;
- (NSURLRequest *)request:(NSString *)path body:(NSDictionary *)body token:(NSString *)token timeout:(NSTimeInterval)timeout;
@end
