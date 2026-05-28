using UnityEngine.UIElements;
var allDocs = UnityEngine.Resources.FindObjectsOfTypeAll<UIDocument>();
var doc = allDocs.Length > 0 ? allDocs[0] : null;
if (doc == null) return "no UIDocument";
var root = doc.rootVisualElement;
var name = parameters["param0"] as string;
Button btn = root.Q<Button>(name);
if (btn == null) return "button not found: " + name;
using (var e = ClickEvent.GetPooled())
{
    e.target = btn;
    btn.SendEvent(e);
}
return "clicked " + btn.name;
