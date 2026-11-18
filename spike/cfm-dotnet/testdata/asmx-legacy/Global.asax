<%@ Application Language="C#" %>

<script runat="server">
    void Application_Start(object sender, EventArgs e)
    {
        System.Web.Routing.RouteTable.Routes.Add(
            "RestApiRoute",
            new System.Web.Routing.Route("api/{*pathInfo}", new ApiRouteHandler()));
    }
</script>
