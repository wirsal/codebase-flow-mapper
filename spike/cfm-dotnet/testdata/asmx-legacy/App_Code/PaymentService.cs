using System.Web.Services;

[WebService(Namespace = "http://example.org/")]
public class PaymentService : WebService
{
    PaymentController paymentController = new PaymentController();

    [WebMethod]
    public PayResponse Pay(PayRequest req)
    {
        return paymentController.Pay(req);
    }

    [WebMethod(MessageName = "Status")]
    public string GetStatus(string id)
    {
        return paymentController.Status(id);
    }

    public string NotExposed()
    {
        return "not a web method";
    }
}
