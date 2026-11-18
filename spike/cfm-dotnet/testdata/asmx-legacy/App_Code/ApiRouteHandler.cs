using System.Web;
using System.Web.Routing;

public sealed class ApiRouteHandler : IRouteHandler
{
    public IHttpHandler GetHttpHandler(RequestContext requestContext)
    {
        return new PaymentApi();
    }
}
