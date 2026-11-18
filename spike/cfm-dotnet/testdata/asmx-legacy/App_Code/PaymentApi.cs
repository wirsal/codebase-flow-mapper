using System.Web;

public class PaymentApi : IHttpHandler
{
    Route routes;
    InquiryAdapter inquiryAdapter = new InquiryAdapter();

    public bool IsReusable { get { return false; } }

    public void ProcessRequest(HttpContext context)
    {
        if (!GetRoutes().Dispatch(context))
        {
            context.Response.StatusCode = 404;
        }
    }

    Route GetRoutes()
    {
        if (routes != null)
        {
            return routes;
        }
        routes = new Route();
        routes.Get("/health", Health);
        routes.Post("/inquiry", inquiryAdapter.Handle);
        return routes;
    }

    static void Health(HttpContext context)
    {
        context.Response.Write("ok");
    }
}
