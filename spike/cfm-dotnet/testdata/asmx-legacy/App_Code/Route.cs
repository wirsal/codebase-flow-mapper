using System;
using System.Collections.Generic;
using System.Web;

// A homegrown route table, the way some .ashx handlers dispatch by path.
public sealed class Route
{
    readonly Dictionary<string, Action<HttpContext>> handlers = new Dictionary<string, Action<HttpContext>>();

    public void Get(string path, Action<HttpContext> handler)
    {
        handlers["GET " + path] = handler;
    }

    public void Post(string path, Action<HttpContext> handler)
    {
        handlers["POST " + path] = handler;
    }

    public bool Dispatch(HttpContext context)
    {
        Action<HttpContext> handler;
        if (!handlers.TryGetValue(context.Request.HttpMethod + " " + context.Request.PathInfo, out handler))
        {
            return false;
        }
        handler(context);
        return true;
    }
}
