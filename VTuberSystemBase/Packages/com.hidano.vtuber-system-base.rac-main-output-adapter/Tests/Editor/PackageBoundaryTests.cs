using System.IO;
using NUnit.Framework;
using RealtimeAvatarController.Core;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditorInternal;
using VTuberSystemBase.RacMainOutputAdapter.Bootstrapper;

namespace VTuberSystemBase.RacMainOutputAdapter.Tests.Editor
{
    /// <summary>
    /// 本 spec の Runtime asmdef が「禁止される依存」（character-selection-tab Runtime / 他タブ Runtime /
    /// 他出力アダプタ Runtime / core-ipc-foundation 具体実装 / ui-toolkit-shell）を参照していないことを検証する
    /// （Requirement 1.2）。
    /// </summary>
    [TestFixture]
    public sealed class PackageBoundaryTests
    {
        private const string RuntimeAsmdefAssetPath =
            "Packages/com.hidano.vtuber-system-base.rac-main-output-adapter/Runtime/VTuberSystemBase.RacMainOutputAdapter.Runtime.asmdef";

        private static readonly string[] ForbiddenAssemblyNames =
        {
            "VTuberSystemBase.CharacterSelectionTab.Runtime",
            "VTuberSystemBase.StageLightingVolumeTab.Runtime",
            "VTuberSystemBase.CameraSwitcherTab.Runtime",
            "VTuberSystemBase.UiToolkitShell.Runtime",
            "VTuberSystemBase.CoreIpc.Core",
            "VTuberSystemBase.OutputRendererShell.Internal",
        };

        [Test]
        public void RuntimeAsmdef_DoesNotReferenceForbiddenAssemblies()
        {
            var asset = AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(RuntimeAsmdefAssetPath);
            Assert.That(asset, Is.Not.Null, $"Runtime asmdef not found at: {RuntimeAsmdefAssetPath}");
            var json = asset.text;
            foreach (var name in ForbiddenAssemblyNames)
            {
                Assert.That(json, Does.Not.Contain(name),
                    $"Runtime asmdef must NOT reference forbidden assembly '{name}'.");
            }
        }

        [Test]
        public void Bootstrapper_ExposesReadOnlySlotManagerProperty()
        {
            var property = typeof(RacMainOutputAdapterBootstrapper).GetProperty(nameof(RacMainOutputAdapterBootstrapper.SlotManager));

            Assert.That(property, Is.Not.Null);
            Assert.That(property!.PropertyType, Is.EqualTo(typeof(SlotManager)));
            Assert.That(property.CanRead, Is.True);
            Assert.That(property.CanWrite, Is.False);
        }
    }
}
