using Microsoft.JSInterop;

namespace Gateway.Server.Components;

/// <summary>
/// 브라우저 확인창(window.confirm). 운용자가 현장 확인으로 창을 오래 열어 둘 수 있으므로 기본 1분 제한 대신 10분까지 기다리고,
/// 시간 초과·연결 끊김·JS 오류는 '취소'로 본다 — 예외가 화면 오류 상자로 번지지 않게.
/// </summary>
public static class JsConfirm
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public static async Task<bool> ConfirmAsync(this IJSRuntime js, string text)
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            return await js.InvokeAsync<bool>("confirm", cts.Token, text);
        }
        catch (Exception e) when (e is OperationCanceledException or JSDisconnectedException or JSException)
        {
            return false;
        }
    }
}
