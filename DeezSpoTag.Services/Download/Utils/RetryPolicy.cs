using System.Net;
using System.Net.Sockets;
using System.Linq;

namespace DeezSpoTag.Services.Download.Utils;

public static class RetryPolicy
{
    public static bool IsTransient(Exception ex)
    {
        if (ex is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(IsTransient);
        }

        if (ex is HttpRequestException
            {
                StatusCode: HttpStatusCode.TooManyRequests
                    or HttpStatusCode.RequestTimeout
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout
            })
        {
            return true;
        }

        if (ex is TaskCanceledException or TimeoutException)
        {
            return true;
        }

        if (ex is IOException ioEx && ioEx.InnerException is SocketException)
        {
            return true;
        }

        return ex.InnerException != null && IsTransient(ex.InnerException);
    }
}
