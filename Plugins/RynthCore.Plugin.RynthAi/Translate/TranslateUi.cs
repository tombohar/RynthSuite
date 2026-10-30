// TranslateUi.cs — ImGui views for the chat translator. Render thread only.
//
//   RenderWindow      "Chat Translate" window: language row, log, compose box, channels, settings
//   RenderSettings    provider / key / languages / options (also the Advanced Settings > Translate page)
//   RenderHubSection  compact on/off + receive/send swap row for the Mini Remote
//
// Settings fields are edited in place (single-value writes the pump thread tolerates); anything that
// needs the pump thread goes through ChatTranslator's thread-safe enqueue methods.
using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.Translate;

internal sealed class TranslateUi
{
    private static readonly Vector4 ColIn = new(0.45f, 0.85f, 0.95f, 1f);
    private static readonly Vector4 ColOut = new(0.40f, 0.95f, 0.45f, 1f);
    private static readonly Vector4 ColTest = new(0.95f, 0.80f, 0.35f, 1f);
    private static readonly Vector4 ColError = new(0.95f, 0.45f, 0.40f, 1f);
    private static readonly Vector4 ColDim = new(0.60f, 0.64f, 0.70f, 1f);

    private readonly ChatTranslator _tr;
    private string _composeText = string.Empty;
    private string _tellTarget = string.Empty;
    private string _testText = string.Empty;
    private TranslateChannel _composeChannel = TranslateChannel.Fellowship;
    private bool _showKey;
    private bool _refocusCompose;
    private int _lastLogCount;

    public TranslateUi(ChatTranslator translator) => _tr = translator;

    private TranslateSettings S => _tr.Settings;

    // ── Window ─────────────────────────────────────────────────────────────

    public void RenderWindow()
    {
        var s = S;
        if (!s.ShowWindow) return;

        ImGui.SetNextWindowSize(new Vector2(520, 420), ImGuiCond.FirstUseEver);
        bool open = true;
        bool visible = ImGui.Begin("Chat Translate##rynthtranslate", ref open, ImGuiWindowFlags.NoCollapse);
        if (!open) s.ShowWindow = false;
        if (!visible) { ImGui.End(); return; }

        RenderHeader();
        RenderLanguageRow("##win", 110f);
        ImGui.Separator();

        if (ImGui.BeginTabBar("##translatetabs"))
        {
            if (ImGui.BeginTabItem("Log")) { RenderLog(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Compose")) { RenderCompose(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Channels")) { RenderChannels(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Settings")) { RenderSettings("##win"); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        ImGui.End();
    }

    private void RenderHeader()
    {
        var s = S;
        ImGui.Checkbox("Enabled##trenabled", ref s.Enabled);
        ImGui.SameLine();
        ImGui.Checkbox("Incoming##trin", ref s.EnableInbound);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Translate chat you receive into your receive language.");
        ImGui.SameLine();
        ImGui.Checkbox("Outgoing##trout", ref s.EnableOutbound);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Translate what you type on the ticked Send channels before it goes out.\n"
                             + $"Start a line with '{s.RawPrefix}' to send it untranslated.");
        ImGui.SameLine();
        SafeImGui.TextColored(ColDim, $"| {_tr.Translated} done, {_tr.CharsSent:N0} chars, {_tr.Pending} queued");

        if (s.ApiKey.Length == 0 && s.Provider != TranslateSettings.ProviderLibre)
            ImGui.TextColored(ColError, "No API key: open the Settings tab to add one.");
        else if (_tr.LastError.Length > 0)
            SafeImGui.TextColored(ColError, "Last error: " + _tr.LastError);
    }

    /// <summary>Receive / swap / send / reset-to-default row (window and Advanced page).</summary>
    public void RenderLanguageRow(string id, float comboWidth)
    {
        var s = S;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Receive");
        ImGui.SameLine();
        string recv = s.MyLanguage;
        if (TranslateLanguages.Combo("##recv" + id, ref recv, allowAuto: false, comboWidth)) s.MyLanguage = recv;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Incoming chat is translated into this language.");
        ImGui.SameLine();
        if (ImGui.Button("<->##swap" + id)) _tr.SwapLanguages();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Swap the receive and send languages.");
        ImGui.SameLine();
        ImGui.TextUnformatted("Send");
        ImGui.SameLine();
        string send = s.CurrentSendLanguage;
        if (TranslateLanguages.Combo("##send" + id, ref send, allowAuto: false, comboWidth)) s.CurrentSendLanguage = send;
        if (ImGui.IsItemHovered()) SafeImGui.SetTooltip($"What you type is translated into this language.\nDefault: {TranslateLanguages.Label(s.DefaultSendLanguage)}");
        ImGui.SameLine();
        bool atDefault = s.CurrentSendLanguage == s.DefaultSendLanguage;
        if (atDefault) ImGui.BeginDisabled();
        if (ImGui.Button("Default##senddef" + id)) s.CurrentSendLanguage = s.DefaultSendLanguage;
        if (atDefault) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            SafeImGui.SetTooltip($"Reset the send language to the default ({s.DefaultSendLanguage}).");
    }

    private void RenderLog()
    {
        var log = _tr.LogSnapshot();
        if (ImGui.SmallButton("Clear")) _tr.ClearLog();
        ImGui.SameLine();
        SafeImGui.TextColored(ColDim, $"{log.Length} entries (hover a line for the original)");

        if (!ImGui.BeginChild("##trlog", new Vector2(0, 0), ImGuiChildFlags.Borders)) { ImGui.EndChild(); return; }
        foreach (var e in log)
        {
            var (tag, col) = e.Direction switch
            {
                TranslateDirection.In => ("IN ", ColIn),
                TranslateDirection.Out => ("OUT", ColOut),
                _ => ("TST", ColTest),
            };
            SafeImGui.TextColored(ColDim, e.Time.ToString("HH:mm:ss"));
            ImGui.SameLine();
            SafeImGui.TextColored(e.Error.Length > 0 && e.Translated.Length == 0 ? ColError : col, tag);
            ImGui.SameLine();
            string ch = e.Channel is { } c ? $"[{TranslateChannels.Name(c)}] " : string.Empty;
            string who = e.Speaker.Length > 0 ? e.Speaker + ": " : string.Empty;
            string body = e.Translated.Length > 0 ? e.Translated : e.Original;
            SafeImGui.TextWrapped($"{ch}{who}{body}");
            if (ImGui.IsItemHovered())
            {
                string langs = e.From.Length > 0 || e.To.Length > 0 ? $"\n{(e.From.Length > 0 ? e.From : "?")} -> {e.To}" : string.Empty;
                SafeImGui.SetTooltip($"Original: {e.Original}{langs}{(e.Error.Length > 0 ? "\n" + e.Error : string.Empty)}");
            }
        }
        // Follow new lines unless the user scrolled up.
        if (log.Length != _lastLogCount && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20f)
            ImGui.SetScrollHereY(1f);
        _lastLogCount = log.Length;
        ImGui.EndChild();
    }

    private void RenderCompose()
    {
        var s = S;
        SafeImGui.TextWrapped($"Type in any language; it is translated into {TranslateLanguages.Label(s.CurrentSendLanguage)} and sent on the chosen channel.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(140f);
        if (ImGui.BeginCombo("Channel##trcompose", TranslateChannels.Name(_composeChannel)))
        {
            foreach (var ch in TranslateChannels.All)
                if (ImGui.Selectable(TranslateChannels.Name(ch), ch == _composeChannel)) _composeChannel = ch;
            ImGui.EndCombo();
        }
        if (_composeChannel == TranslateChannel.Tell)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160f);
            ImGui.InputTextWithHint("##trtell", "name (blank = reply)", ref _tellTarget, 64u);
        }

        ImGui.SetNextItemWidth(-1f);
        if (_refocusCompose)
        {
            ImGui.SetKeyboardFocusHere();
            _refocusCompose = false;
        }
        bool enter = ImGui.InputTextWithHint("##trcomposetext", "message", ref _composeText, 400u, ImGuiInputTextFlags.EnterReturnsTrue);
        if (ImGui.Button("Translate && send") || enter)
        {
            if (_composeText.Trim().Length > 0)
            {
                _tr.Compose(_composeChannel, _tellTarget, _composeText);
                _composeText = string.Empty;
                _refocusCompose = enter;
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Preview only")) _tr.Test(_composeText);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Translate without sending; the result appears in the Log tab and chat.");
        if (!s.Enabled) ImGui.TextColored(ColDim, "The compose box works even while auto-translate is off.");
    }

    private void RenderChannels()
    {
        var s = S;
        ImGui.TextWrapped("Listen: incoming lines on these channels are translated into your receive language. "
                          + "Send: lines you type on these channels are translated into your send language.");
        if (ImGui.SmallButton("Listen all")) s.InboundMask = AllMask();
        ImGui.SameLine();
        if (ImGui.SmallButton("Listen none")) s.InboundMask = 0;
        ImGui.SameLine();
        if (ImGui.SmallButton("Send all")) s.OutboundMask = AllMask();
        ImGui.SameLine();
        if (ImGui.SmallButton("Send none")) s.OutboundMask = 0;
        ImGui.SameLine();
        if (ImGui.SmallButton("Defaults"))
        {
            s.InboundMask = TranslateSettings.DefaultInboundMask();
            s.OutboundMask = TranslateSettings.DefaultOutboundMask();
        }

        if (!ImGui.BeginTable("##trchannels", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("Channel", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Listen", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableSetupColumn("Send", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableHeadersRow();
        foreach (var ch in TranslateChannels.All)
        {
            uint bit = TranslateChannels.Bit(ch);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(TranslateChannels.Name(ch));
            if (ch == TranslateChannel.Local && ImGui.IsItemHovered())
                ImGui.SetTooltip("Local speech includes NPCs; listening here uses more of your API quota.");
            ImGui.TableNextColumn();
            bool inOn = (s.InboundMask & bit) != 0;
            if (ImGui.Checkbox("##in" + (int)ch, ref inOn)) s.InboundMask = inOn ? s.InboundMask | bit : s.InboundMask & ~bit;
            ImGui.TableNextColumn();
            bool outOn = (s.OutboundMask & bit) != 0;
            if (ImGui.Checkbox("##out" + (int)ch, ref outOn)) s.OutboundMask = outOn ? s.OutboundMask | bit : s.OutboundMask & ~bit;
        }
        ImGui.EndTable();
    }

    private static uint AllMask()
    {
        uint m = 0;
        foreach (var ch in TranslateChannels.All) m |= TranslateChannels.Bit(ch);
        return m;
    }

    // ── Settings (window tab + Advanced Settings page) ─────────────────────

    public void RenderSettings(string id)
    {
        var s = S;

        ImGui.TextColored(ColIn, "Service");
        int provider = Math.Max(0, Array.IndexOf(TranslateSettings.Providers, s.Provider));
        ImGui.SetNextItemWidth(220f);
        if (ImGui.Combo("Provider" + id, ref provider, TranslateSettings.ProviderNames, TranslateSettings.ProviderNames.Length))
            s.Provider = TranslateSettings.Providers[provider];
        SafeImGui.TextColored(ColDim, ProviderHint(s.Provider));

        var keyFlags = _showKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password;
        ImGui.SetNextItemWidth(300f);
        ImGui.InputTextWithHint("API key" + id, s.Provider == TranslateSettings.ProviderLibre ? "optional" : "required",
            ref s.ApiKey, 256u, keyFlags);
        ImGui.SameLine();
        ImGui.Checkbox("Show" + id + "key", ref _showKey);

        ImGui.SetNextItemWidth(300f);
        ImGui.InputTextWithHint("Endpoint" + id, TranslateClient.DefaultUrl(s.Provider, s.ApiKey), ref s.ApiUrl, 256u);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Leave blank for the provider's default.\nLibreTranslate: your server URL, e.g. http://localhost:5000");

        ImGui.Spacing();
        ImGui.TextColored(ColIn, "Languages");
        string my = s.MyLanguage;
        if (TranslateLanguages.Combo("Receive (my language)" + id, ref my, allowAuto: false, 180f)) s.MyLanguage = my;
        string src = s.InboundSource;
        if (TranslateLanguages.Combo("Incoming chat is in" + id, ref src, allowAuto: true, 180f)) s.InboundSource = src;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Auto-detect works for mixed channels; pick a language to force it.");
        string def = s.DefaultSendLanguage;
        if (TranslateLanguages.Combo("Default send language" + id, ref def, allowAuto: false, 180f))
        {
            // Following the default keeps the hub in step unless the user picked something else this session.
            if (s.CurrentSendLanguage == s.DefaultSendLanguage) s.CurrentSendLanguage = def;
            s.DefaultSendLanguage = def;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Each login starts with this send language; swap it any time from the Mini Remote.");

        ImGui.Spacing();
        ImGui.TextColored(ColIn, "Behaviour");
        ImGui.Checkbox("Skip my own lines" + id, ref s.SkipOwn);
        ImGui.Checkbox("Write incoming translations to chat" + id, ref s.EchoToChat);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shown as \"[Translate] [Channel] Name (lang): text\" in AC chat and RynthChat,\nin the same tab as the original. Always kept in the Translate log.");
        ImGui.Checkbox("Show what I typed after sending a translation" + id, ref s.EchoOutbound);
        ImGui.Checkbox("Send the original if translation fails" + id, ref s.SendOriginalOnError);
        ImGui.SetNextItemWidth(60f);
        ImGui.InputText("Send-untranslated prefix" + id, ref s.RawPrefix, 4u);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("A typed line starting with this goes out as-is (prefix removed).");
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Min ms between requests" + id, ref s.MinIntervalMs, 250, 5000)) s.MinIntervalMs = Math.Clamp(s.MinIntervalMs, 250, 10000);
        ImGui.SetNextItemWidth(180f);
        if (ImGui.SliderInt("Log size" + id, ref s.MaxLog, 20, 1000)) s.MaxLog = Math.Clamp(s.MaxLog, 20, 2000);

        ImGui.Spacing();
        ImGui.TextColored(ColIn, "Test");
        ImGui.SetNextItemWidth(260f);
        bool go = ImGui.InputTextWithHint("##trtest" + id, "text to translate", ref _testText, 400u, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Test" + id) || go) && _testText.Trim().Length > 0) _tr.Test(_testText);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Translates into the current send language and prints the result in chat.");

        ImGui.Spacing();
        ImGui.TextColored(ColDim, "Shared by all characters. The API key is stored in plain text in RynthAi\\translate.json.");
        ImGui.TextColored(ColDim, "AC's chat font can't draw CJK / Thai / Arabic script; those show as boxes.");
    }

    private static string ProviderHint(string provider) => provider switch
    {
        TranslateSettings.ProviderDeepL => "DeepL API key (deepl.com/pro-api). Free plan keys end in :fx, 500k chars/month.",
        TranslateSettings.ProviderLibre => "Self-hosted or public LibreTranslate server; libretranslate.com needs a key.",
        _ => "Cloud Translation API key (console.cloud.google.com), 500k chars/month free.",
    };

    // ── Mini Remote section ────────────────────────────────────────────────

    public void RenderHubSection()
    {
        var s = S;
        ImGui.PushID("##mrtranslate");
        ImGui.Checkbox("Translate", ref s.Enabled);
        if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(_tr.StatusLine() + (_tr.LastError.Length > 0 ? "\nLast error: " + _tr.LastError : string.Empty));
        ImGui.SameLine();
        if (ImGui.SmallButton("Window")) s.ShowWindow = !s.ShowWindow;

        string recv = s.MyLanguage;
        if (TranslateLanguages.Combo("##mrrecv", ref recv, allowAuto: false, 62f)) s.MyLanguage = recv;
        if (ImGui.IsItemHovered()) SafeImGui.SetTooltip($"Receive: {TranslateLanguages.Label(s.MyLanguage)}");
        ImGui.SameLine();
        if (ImGui.Button("<->")) _tr.SwapLanguages();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Swap receive and send languages.");
        ImGui.SameLine();
        string send = s.CurrentSendLanguage;
        if (TranslateLanguages.Combo("##mrsend", ref send, allowAuto: false, 62f)) s.CurrentSendLanguage = send;
        if (ImGui.IsItemHovered()) SafeImGui.SetTooltip($"Send: {TranslateLanguages.Label(s.CurrentSendLanguage)}");
        // Right-click belongs to the Mini Remote options menu, so the reset is a button.
        if (s.CurrentSendLanguage != s.DefaultSendLanguage)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("def")) s.CurrentSendLanguage = s.DefaultSendLanguage;
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip($"Reset the send language to the default ({s.DefaultSendLanguage}).");
        }
        ImGui.PopID();
    }
}
