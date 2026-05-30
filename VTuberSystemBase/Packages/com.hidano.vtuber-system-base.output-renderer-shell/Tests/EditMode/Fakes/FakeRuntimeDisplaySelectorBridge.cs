#nullable enable
using System;
using System.Collections.Generic;
using UnityEngine;
using VTuberSystemBase.OutputRendererShell.Display;

namespace VTuberSystemBase.OutputRendererShell.EditModeTests.Fakes
{
    /// <summary>
    /// <see cref="IRuntimeDisplaySelectorBridge"/> のテスト用フェイク。
    /// <c>RuntimeDisplaySelector.Current</c> に直接依存せずに <see cref="RuntimeDisplaySelectorRoutingService"/> を検証するため。
    /// </summary>
    public sealed class FakeRuntimeDisplaySelectorBridge : IRuntimeDisplaySelectorBridge
    {
        public readonly struct AssignCall
        {
            public Camera Camera { get; }
            public int DisplayIndex { get; }
            public string? SpoutSenderName { get; }
            public AssignCall(Camera camera, int displayIndex, string? spoutSenderName)
            {
                Camera = camera;
                DisplayIndex = displayIndex;
                SpoutSenderName = spoutSenderName;
            }
        }

        public readonly struct RenderTextureAssignCall
        {
            public RenderTexture RenderTexture { get; }
            public int DisplayIndex { get; }
            public RenderTextureAssignCall(RenderTexture renderTexture, int displayIndex)
            {
                RenderTexture = renderTexture;
                DisplayIndex = displayIndex;
            }
        }

        private readonly List<AssignCall> _calls = new();
        private readonly List<RenderTextureAssignCall> _renderTextureCalls = new();

        /// <summary>RDS Facade が利用可能かを偽装する。既定 <c>true</c>。</summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>AssignCameraToDisplay / AssignRenderTextureToDisplay 呼び出し時に投げる例外。null の場合は投げない。</summary>
        public Exception? ThrowOnAssign { get; set; }

        /// <summary>AssignCameraToDisplay 呼び出し履歴。</summary>
        public IReadOnlyList<AssignCall> Calls => _calls;

        /// <summary>AssignRenderTextureToDisplay 呼び出し履歴。</summary>
        public IReadOnlyList<RenderTextureAssignCall> RenderTextureCalls => _renderTextureCalls;

        public void AssignCameraToDisplay(Camera camera, int displayIndex, string? spoutSenderName)
        {
            _calls.Add(new AssignCall(camera, displayIndex, spoutSenderName));
            if (ThrowOnAssign != null)
            {
                throw ThrowOnAssign;
            }
            // RDS 既定挙動の模倣: targetDisplay を設定する。
            camera.targetDisplay = displayIndex;
        }

        public void AssignRenderTextureToDisplay(RenderTexture renderTexture, int displayIndex)
        {
            _renderTextureCalls.Add(new RenderTextureAssignCall(renderTexture, displayIndex));
            if (ThrowOnAssign != null)
            {
                throw ThrowOnAssign;
            }
            // RDS は RenderTexture を Texture モードで Spout 送出する。カメラの targetTexture は呼び出し側が設定する。
        }
    }
}
