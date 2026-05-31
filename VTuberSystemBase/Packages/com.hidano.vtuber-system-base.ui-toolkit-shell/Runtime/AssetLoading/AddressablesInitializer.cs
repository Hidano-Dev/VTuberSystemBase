#nullable enable
using System;
using System.Linq;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace VTuberSystemBase.UiToolkitShell.AssetLoading
{
    /// <summary>
    /// Production <see cref="IAddressablesInitializer"/> backed by
    /// <see cref="Addressables.InitializeAsync()"/>. The Addressables operation's
    /// <c>Completed</c> event is documented to fire on the Unity main thread, which
    /// satisfies the bootstrap contract that the result callback is delivered without
    /// thread marshalling (Requirement 4.3 / 11.3 alignment).
    /// </summary>
    /// <remarks>
    /// Two failure surfaces are translated into <see cref="AddressablesInitResult.Fail"/>:
    /// (1) a synchronous exception thrown from <c>InitializeAsync()</c> itself (rare; would
    /// indicate Addressables is misconfigured to the point that scheduling the operation
    /// fails), and (2) the asynchronous <c>AsyncOperationStatus.Failed</c> outcome reported
    /// via the operation handle. Both paths surface as <see cref="BootstrapErrorCode.AddressablesInitFailed"/>
    /// once they reach <see cref="AddressablesBootstrap"/>.
    /// </remarks>
    public sealed class AddressablesInitializer : IAddressablesInitializer
    {
        public void InitializeAsync(Action<AddressablesInitResult> onCompleted)
        {
            if (onCompleted is null) throw new ArgumentNullException(nameof(onCompleted));

            // 再初期化レース対策（Enter Play Mode の DisableDomainReload 下で顕在化）:
            // Domain Reload が無効だと Addressables の static 状態が PlayMode セッションをまたいで
            // 残り、2 回目以降の InitializeAsync() が中途半端な内部状態に対して同期例外
            // (ArgumentOutOfRangeException: Index was out of range) を投げることがある。
            // 既にロケータが登録済み＝初期化済みなら、再 init を呼ばずに成功として扱い、その
            // throw を構造的に回避する。ResourceLocators の参照自体が不整合状態で投げても
            // 安全側（未初期化扱い）にフォールバックして通常の init パスへ進む。
            try
            {
                if (Addressables.ResourceLocators.Any())
                {
                    onCompleted(AddressablesInitResult.Ok());
                    return;
                }
            }
            catch
            {
                // ResourceLocators 列挙が投げた場合は未初期化とみなして init を試みる。
            }

            AsyncOperationHandle<UnityEngine.AddressableAssets.ResourceLocators.IResourceLocator> handle;
            try
            {
                handle = Addressables.InitializeAsync();
            }
            catch (Exception ex)
            {
                onCompleted(AddressablesInitResult.Fail(ex,
                    $"Addressables.InitializeAsync threw before scheduling: {ex.GetType().Name}: {ex.Message}"));
                return;
            }

            handle.Completed += op =>
            {
                if (op.Status == AsyncOperationStatus.Succeeded)
                {
                    onCompleted(AddressablesInitResult.Ok());
                }
                else
                {
                    var ex = op.OperationException;
                    onCompleted(AddressablesInitResult.Fail(ex,
                        ex?.Message ?? "Addressables.InitializeAsync reported AsyncOperationStatus.Failed"));
                }
            };
        }
    }
}
