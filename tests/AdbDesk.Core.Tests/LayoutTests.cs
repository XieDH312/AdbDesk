using AdbDesk.Core;

internal static class LayoutTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <hierarchy rotation="1">
          <node index="0" package="com.example" class="android.widget.FrameLayout" bounds="[0,0][800,400]">
            <node index="0" text="保存 &amp; 退出" resource-id="com.example:id/save" class="android.widget.Button" clickable="true" bounds="[10,20][110,70]" />
            <node index="1" text="重复" class="android.widget.TextView" bounds="[10,20][110,70]" />
            <node index="2" bounds="[200,200][100,100]" />
          </node>
        </hierarchy>
        """;

    public static Task ParserAsync()
    {
        var tree = LayoutHierarchy.Parse(Sample);
        Check(tree.Rotation == 1 && tree.Nodes.Count == 4 && tree.Roots.Count == 1, "hierarchy and rotation");
        var button = tree.Nodes[1];
        Check(button.Text == "保存 & 退出" && button.ResourceId == "com.example:id/save", "XML attributes decoded");
        Check(button.Bounds is { Width: 100, Height: 50 } && button.Parent == tree.Roots[0], "bounds and parent");
        Check(tree.HitTest(20, 30) == tree.Nodes[2], "overlapping same-size siblings select later drawing order");
        Check(tree.HitTest(799, 399) == tree.Roots[0] && tree.HitTest(800, 400) is null, "half-open bounds");
        Check(tree.Nodes[3].Bounds is null, "invalid bounds cannot produce overlays");
        Check(button.Matches("SAVE") && button.Matches("退出"), "search ID and decoded text");
        Reject("<!DOCTYPE hierarchy [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><hierarchy><node text='&x;'/></hierarchy>");
        Reject("<unexpected><node/></unexpected>");
        Reject("<hierarchy><node></hierarchy>");
        Reject("<hierarchy>" + string.Concat(Enumerable.Repeat("<node>", 130)) + string.Concat(Enumerable.Repeat("</node>", 130)) + "</hierarchy>");
        Check(LayoutHierarchy.Parse("<hierarchy rotation='0'/>").Nodes.Count == 0, "valid empty tree remains distinguishable from a read error");
        return Task.CompletedTask;
    }

    public static async Task CaptureAsync()
    {
        var calls = new List<ConsoleCommand>();
        var snapshot = await new LayoutInspectionService((command, token) =>
        {
            calls.Add(command);
            return Task.FromResult(command.HistoryText.StartsWith("shell cat ") ? Sample : "");
        }, (serial, token) => Task.FromResult(new byte[] { 1, 2, 3 })).CaptureAsync("phone-A", default);
        Check(snapshot.Hierarchy?.Nodes.Count == 4 && snapshot.Png?.Length == 3, "capture combines XML and image");
        Check(calls.Count == 3 && calls.All(c => c.Arguments.Take(2).SequenceEqual(new[] { "-s", "phone-A" })), "dump read cleanup only target selected device");
        var dumpPath = calls[0].HistoryText.Split(' ').Last();
        Check(dumpPath.StartsWith("/data/local/tmp/adbdesk-layout-") && dumpPath.EndsWith(".xml"), "unique app-owned temporary file");
        Check(calls[1].HistoryText == "shell cat " + dumpPath && calls[2].HistoryText == "shell rm -f " + dumpPath, "read and remove exactly that temporary file");

        calls.Clear();
        var partial = await new LayoutInspectionService((command, token) =>
        {
            calls.Add(command);
            if (!command.HistoryText.StartsWith("shell rm ")) throw new IOException("no idle state");
            return Task.FromResult("");
        }, (serial, token) => Task.FromResult(new byte[] { 9 })).CaptureAsync("phone-B", default);
        Check(partial.Hierarchy is null && partial.Png is not null && partial.Warnings.Count == 1, "layout failure still allows screenshot with explicit reason");
        Check(calls.Last().HistoryText.StartsWith("shell rm -f "), "cleanup after failed dump");

        using var canceled = new CancellationTokenSource();
        calls.Clear();
        var service = new LayoutInspectionService((command, token) =>
        {
            calls.Add(command);
            if (command.HistoryText.StartsWith("shell uiautomator ")) { canceled.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.FromResult("");
        }, (serial, token) => throw new Exception("screenshot must not start after cancellation"));
        try { await service.CaptureAsync("phone-C", canceled.Token); throw new Exception("expected cancellation"); }
        catch (OperationCanceledException) { }
        Check(calls.Count == 2 && calls.Last().HistoryText.StartsWith("shell rm -f "), "cancellation cleanup has independent bounded token");
    }

    private static void Reject(string xml)
    {
        try { LayoutHierarchy.Parse(xml); }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException) { return; }
        throw new Exception("unsafe or invalid XML was accepted");
    }
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
}
