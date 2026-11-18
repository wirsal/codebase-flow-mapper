using System.IO;
using System.Web;
using System.Web.Script.Serialization;

public class InquiryAdapter
{
    PaymentController paymentController = new PaymentController();

    public void Handle(HttpContext context)
    {
        string body = new StreamReader(context.Request.InputStream).ReadToEnd();
        InquiryRequest req = new JavaScriptSerializer().Deserialize<InquiryRequest>(body);
        if (req.Kind == "status" && req.Tenor >= 12)
        {
            context.Response.Write(paymentController.Status(req.Kind));
            return;
        }
        context.Response.StatusCode = 400;
    }
}
