// ExpressionEngine.RaNav.cs - ranav[]: the nav route RynthAi has loaded, as one JSON line, for a script that
// runs routes of its own (2026-11-18: a route for a Thread where the explore can't go,
// then a second one looped from where the first ends):
//   {"name":"Pit00C1In","points":24,"index":3,"type":"Once","finished":false,"nav":true,"macro":true}
// name is the file's name without .nav ("" for a route made on the fly: /ra dunnav-to, a GoTo, a meta's nav);
// finished is a Once route walked to its end (the meta's "nav route empty").
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RynthCore.Plugin.RynthAi.Meta;

internal sealed partial class ExpressionEngine
{
    private string EvalRaNav()
    {
        var s = _settings;
        if (s == null) return "{}";
        var route = s.CurrentRoute;
        string path = s.CurrentNavPath ?? string.Empty;
        string name = path.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(path) : string.Empty;
        var sb = new StringBuilder(160);
        sb.Append("{\"name\":\"").Append(JsonEncodedText.Encode(name).ToString()).Append('"');
        sb.Append(",\"points\":").Append((route?.Points.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"index\":").Append(s.ActiveNavIndex.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"type\":\"").Append(route?.RouteType.ToString() ?? "None").Append('"');
        sb.Append(",\"finished\":").Append(s.NavRouteFinished() ? "true" : "false");
        sb.Append(",\"nav\":").Append(s.EnableNavigation ? "true" : "false");
        sb.Append(",\"macro\":").Append(s.IsMacroRunning ? "true" : "false");
        return sb.Append('}').ToString();
    }
}
