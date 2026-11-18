using System.IO;
using System.Net;

public class EsbClient
{
    public const string EsbBase = "http://esb.example.internal/invoke/";

    public string Send(string service, string payload)
    {
        return Post(service, payload);
    }

    public static string Post(string service, string payload)
    {
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(EsbBase + service);
        request.Method = "POST";
        using (StreamWriter writer = new StreamWriter(request.GetRequestStream()))
        {
            writer.Write(payload);
        }
        using (WebResponse response = request.GetResponse())
        using (StreamReader reader = new StreamReader(response.GetResponseStream()))
        {
            return reader.ReadToEnd();
        }
    }
}
