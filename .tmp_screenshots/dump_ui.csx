var sb = new System.Text.StringBuilder();
var allDocs = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UIElements.UIDocument>();
sb.AppendLine("All UIDocuments = " + allDocs.Length);
foreach (var d in allDocs)
{
    sb.AppendLine("  " + d.gameObject.name + " scene=" + d.gameObject.scene.name + " active=" + d.gameObject.activeInHierarchy + " ps=" + (d.panelSettings != null ? d.panelSettings.name : "null") + " visTree=" + (d.visualTreeAsset != null ? d.visualTreeAsset.name : "null"));
}
var allPs = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.UIElements.PanelSettings>();
sb.AppendLine("All PanelSettings = " + allPs.Length);
foreach (var p in allPs)
{
    sb.AppendLine("  PS name=" + p.name + " targetDisplay=" + p.targetDisplay + " sortingOrder=" + p.sortingOrder + " referenceResolution=" + p.referenceResolution);
}
System.Action<UnityEngine.UIElements.VisualElement,int> dump = null;
dump = (v, depth) => {
    if (v == null) return;
    var size = string.Format("{0:F0}x{1:F0}", v.resolvedStyle.width, v.resolvedStyle.height);
    var pos  = string.Format("({0:F0},{1:F0})", v.worldBound.x, v.worldBound.y);
    var disp = v.resolvedStyle.display.ToString();
    var prefix = new string(' ', depth * 2);
    sb.AppendLine(prefix + "[" + v.GetType().Name + "] name=\"" + v.name + "\" size=" + size + " world=" + pos + " display=" + disp);
    if (depth >= 6) { sb.AppendLine(prefix + "  ..."); return; }
    for (int i = 0; i < v.childCount; i++) dump(v[i], depth + 1);
};
foreach (var d in allDocs)
{
    if (d.rootVisualElement == null) continue;
    sb.AppendLine("=== " + d.gameObject.name + " ===");
    dump(d.rootVisualElement, 0);
}
return sb.ToString();
