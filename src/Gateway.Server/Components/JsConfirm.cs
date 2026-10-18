using Microsoft.JSInterop;

namespace Gateway.Server.Components;

/// <summary>
/// 확인창(app.js 의 gwConfirm — 기본 초점은 취소, 확인 버튼은 동사형 라벨). 운용자가 현장 확인으로 창을 오래 열어 둘 수 있으므로 기본 1분 제한 대신 10분까지 기다리고,
/// 시간 초과·연결 끊김·JS 오류는 '취소'로 본다 — 예외가 화면 오류 상자로 번지지 않게.
/// </summary>
public static class JsConfirm
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <param name="ok">확인 버튼 라벨 — "예"가 아니라 동사("적용 시작")로</param>
    /// <param name="danger">되돌릴 수 없는 동작이면 확인 버튼을 경고색으로</param>
    public static async Task<bool> ConfirmAsync(this IJSRuntime js, string text, string ok = "진행", string cancel = "취소", bool danger = false)
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            return await js.InvokeAsync<bool>("gwConfirm", cts.Token, text, ok, cancel, danger);
        }
        catch (Exception e) when (e is OperationCanceledException or JSDisconnectedException or JSException)
        {
            return false;
        }
    }
}
