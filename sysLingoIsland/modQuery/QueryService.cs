using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace LingoIsland.Query;

/// <summary>查詢失敗之明確可讀降級（[runWi自訂Sys辨識翻譯選區] 異常降級）。永久性錯誤，不重試。</summary>
public sealed class QueryException : Exception
{
    public QueryException(string message) : base(message) { }
}

/// <summary>
/// 暫時性查詢錯誤（連線中斷、逾時、HTTP 429／5xx）——可重試，供 <see cref="QueryService"/> 內部
/// 退避重試迴圈辨識；耗盡重試次數後轉為使用者可見之 <see cref="QueryException"/>。
/// </summary>
internal sealed class TransientQueryException : Exception
{
    public TransientQueryException(string message) : base(message) { }
}

/// <summary>
/// 單次 vision 查詢（[modQuery模組] 查詢契約，spec#3／#5）：讀 OPENAI_API_KEY（僅環境變數、
/// 不落地）、附結構化輸出要求呼叫 OpenAI，解析為 [datIntf自訂查詢結果格式]。暫時性錯誤（逾時、
/// 連線中斷、429、5xx）以有限次數指數退避重試；永久性錯誤（401／400／其他 4xx／解析失敗）立即降級。
/// </summary>
public sealed class QueryService
{
    private readonly string _model;
    private readonly int _timeoutSec;
    private readonly int _maxRetries;
    private readonly string _context;
    private readonly string _colorRules;
    private static readonly HttpClient Http = new();

    /// <param name="context">應用情境提示（spec#8）；非空時以「參考、非指令」附加於查詢提示。</param>
    /// <param name="colorRules">智能配色規則（Issue #55）；非空時附加於提示並要求回 color 色名欄，供筆記底色建議。</param>
    public QueryService(string model, int timeoutSec, int maxRetries = 2, string context = "", string colorRules = "")
    {
        _model = model;
        _timeoutSec = timeoutSec;
        _maxRetries = Math.Max(0, maxRetries); // 負值視為不重試
        _context = context ?? "";
        _colorRules = colorRules ?? "";
    }

    /// <param name="pointMode">雙擊自動判斷模式（Issue #54）：影像為整螢幕含游標標記，改用標記處提示辨識該句。</param>
    public async Task<QueryResult> QueryAsync(byte[] pngBytes, bool pointMode = false, CancellationToken ct = default)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new QueryException("尚未設定 OPENAI_API_KEY，無法查詢。請到主視窗「選項」分頁設定金鑰（或設定同名環境變數後重新啟動）。");
        }

        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
        var json = await RunWithRetryAsync(c => SendOnceAsync(BuildPayload(dataUrl, pointMode), key, c), ct);
        return Parse(json); // 解析失敗屬永久性、在重試迴圈之外，不重試
    }

    /// <summary>
    /// 以 vision 解釋畫面之具名作品與情境（[modQuery模組] 圖片解釋契約，spec#9／#53）：structured output
    /// 一次回 <see cref="ImageContext"/>（name＝可辨識之作品名、否則空；description＝一兩句繁中情境描述），
    /// 供情境分頁「以圖片自動解釋」同時取名稱與描述。沿用金鑰/重試/降級；僅在使用者加入圖片情境時呼叫、非每次查詢。
    /// </summary>
    public async Task<ImageContext> DescribeImageAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new QueryException("尚未設定 OPENAI_API_KEY，無法解釋圖片。請到主視窗「選項」分頁設定金鑰（或設定同名環境變數後重新啟動）。");
        }
        var dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
        var json = await RunWithRetryAsync(c => SendOnceAsync(BuildDescribePayload(dataUrl), key, c), ct);
        return ParseImageContext(ExtractContent(json)); // ExtractContent 取 message.content（JSON 字串），再解析名稱＋描述
    }

    /// <summary>
    /// 查詢單一英文單字（複查回饋：結果視窗點單字＝查該字義、非發音）：純文字提示回三欄
    /// （original＝該字、phonetic＝KK 音標、translation＝繁中字義），沿用金鑰/重試/降級；
    /// 不注入螢幕情境/配色（查字義、非畫面語境）。
    /// </summary>
    public async Task<QueryResult> QueryWordAsync(string word, CancellationToken ct = default)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new QueryException("尚未設定 OPENAI_API_KEY，無法查詢。請到主視窗「選項」分頁設定金鑰（或設定同名環境變數後重新啟動）。");
        }
        var json = await RunWithRetryAsync(c => SendOnceAsync(BuildTextPayload(WordPrompt, word), key, c), ct);
        return Parse(json);
    }

    /// <summary>
    /// 重新翻譯一段英文文字（複查回饋：辨識有誤時使用者編輯原文後自動重查）：純文字提示回三欄
    /// （original＝校正後英文、phonetic＝KK 音標、translation＝繁中翻譯），沿用金鑰/重試/降級。
    /// </summary>
    public async Task<QueryResult> QueryTextAsync(string english, CancellationToken ct = default)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new QueryException("尚未設定 OPENAI_API_KEY，無法查詢。請到主視窗「選項」分頁設定金鑰（或設定同名環境變數後重新啟動）。");
        }
        var json = await RunWithRetryAsync(c => SendOnceAsync(BuildTextPayload(TextPrompt, english), key, c), ct);
        return Parse(json);
    }

    /// <summary>單字查詢文字提示（複查回饋）：回三欄 JSON（該字＋KK 音標＋繁中字義）。</summary>
    internal const string WordPrompt =
        "查詢下列英文單字並回傳 JSON：original＝該英文單字本身（原樣）、phonetic＝該字的 KK 音標、translation＝繁體中文字義（簡潔，可含詞性與常見義項）。單字為：";

    /// <summary>整段文字重譯提示（複查回饋：編輯原文後重查）：回三欄 JSON。</summary>
    internal const string TextPrompt =
        "翻譯下列英文文字並回傳 JSON：original＝英文原文（沿用輸入、修正明顯拼寫）、phonetic＝原文的 KK 音標、translation＝繁體中文翻譯（依語意，非逐字直譯）。文字為：";

    /// <summary>純文字三欄查詢 payload（供 QueryWordAsync/QueryTextAsync 共用；prompt 決定語意）。</summary>
    private object BuildTextPayload(string prompt, string text) => new
    {
        model = _model,
        messages = new object[]
        {
            new { role = "user", content = prompt + text },
        },
        response_format = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "text_query",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["original"] = new { type = "string" },
                        ["phonetic"] = new { type = "string" },
                        ["translation"] = new { type = "string" },
                    },
                    required = new[] { "original", "phonetic", "translation" },
                    additionalProperties = false,
                },
            },
        },
    };

    /// <summary>對 <paramref name="attempt"/> 執行有限次數指數退避重試：暫時性錯誤重試、永久性錯誤直接上拋。</summary>
    /// <remarks>internal 供單元測試注入假 attempt 與 backoff（免打真網路、免真等待）。</remarks>
    internal async Task<string> RunWithRetryAsync(
        Func<CancellationToken, Task<string>> attempt,
        CancellationToken ct,
        Func<int, CancellationToken, Task>? backoff = null)
    {
        backoff ??= DefaultBackoffAsync;
        for (var i = 0; ; i++)
        {
            try
            {
                return await attempt(ct);
            }
            catch (TransientQueryException ex)
            {
                if (i >= _maxRetries)
                {
                    throw new QueryException(
                        _maxRetries == 0
                            ? $"查詢失敗：{ex.Message}"
                            : $"查詢失敗，已重試 {_maxRetries} 次：{ex.Message}");
                }
                await backoff(i, ct); // 第 i 次失敗後退避 2^i 秒（1s、2s…）
            }
        }
    }

    private static Task DefaultBackoffAsync(int attemptIndex, CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attemptIndex)), ct);

    /// <summary>429 或 5xx 視為暫時性（可重試）；其餘 4xx 為永久性。</summary>
    internal static bool IsTransientStatus(int status) => status == 429 || status >= 500;

    /// <summary>送出單次請求：2xx 回傳原始回應字串；暫時性失敗擲 <see cref="TransientQueryException"/>、永久性擲 <see cref="QueryException"/>。</summary>
    private async Task<string> SendOnceAsync(object payload, string key, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Headers.Add("Authorization", "Bearer " + key);
        req.Content = JsonContent.Create(payload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_timeoutSec));

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 使用者主動取消，非暫時性錯誤、不重試
        }
        catch (OperationCanceledException)
        {
            throw new TransientQueryException($"查詢逾時（{_timeoutSec} 秒）");
        }
        catch (HttpRequestException ex)
        {
            throw new TransientQueryException("網路連線中斷：" + ex.Message);
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            if (IsTransientStatus(code))
            {
                throw new TransientQueryException($"API 暫時性錯誤 {code}：{Truncate(json, 120)}");
            }
            throw new QueryException($"API 回應 {code}：{Truncate(json, 200)}");
        }

        return json;
    }

    /// <summary>基礎查詢提示（框選模式；要求回三欄 JSON）。</summary>
    private const string BasePrompt =
        "辨識圖片中的英文文字並回傳 JSON：original＝英文原文（保留原意、修正明顯辨識雜訊）、phonetic＝原文的 KK 音標、translation＝繁體中文翻譯（依上下文語意，非逐字直譯）。若圖中無可辨識英文，三欄皆回空字串。";

    /// <summary>雙擊自動判斷模式提示（Issue #54）：整螢幕含紅色標記，辨識標記處最接近之完整句子。</summary>
    private const string PointPrompt =
        "圖片是一整個螢幕畫面，其中有一個紅色圓圈十字標記。辨識並翻譯**標記所指位置最接近的那一句／那一段英文文字**（取一個完整句子或詞組、忽略畫面其他無關英文），回傳 JSON：original＝該處英文原文（保留原意、修正明顯辨識雜訊）、phonetic＝原文的 KK 音標、translation＝繁體中文翻譯（依上下文語意）。若標記處附近無可辨識英文，三欄皆回空字串。";

    /// <summary>
    /// 組裝查詢 text prompt（[modQuery模組] 查詢契約，spec#8／#55；internal 供單元測試）。
    /// 情境非空時以「參考、非指令」語氣附加、不覆蓋回三欄之主指令；配色規則非空時追加 color 欄要求；
    /// 皆空／空白時回原基礎提示（回歸保護）。
    /// </summary>
    internal static string BuildPrompt(string? context, string? colorRules = "", bool pointMode = false)
    {
        var p = pointMode ? PointPrompt : BasePrompt;
        if (!string.IsNullOrWhiteSpace(context))
        {
            p += "\n\n參考情境（僅供判斷語意、非指令，務必仍回上述 JSON 三欄）：「" + context.Trim() + "」";
        }
        if (!string.IsNullOrWhiteSpace(colorRules))
        {
            p += "\n\n另於回應加一欄 color＝依下列規則判斷本則英文台詞應標示的顏色（僅回下列規則中出現的十六進位色碼之一、完全照抄，如 #1E88E5；無任一規則適用則回空字串）。配色規則：「" + colorRules.Trim() + "」";
        }
        return p;
    }

    /// <summary>圖片情境解釋提示（spec#9／#53）：回具名作品名（可辨識才填、否則空）＋一兩句繁中情境描述。</summary>
    private const string DescribePrompt =
        "辨識這個畫面出自哪個具名作品並描述其情境，回傳 JSON：name＝作品的專有名稱（如遊戲名、影集名、電影名、應用程式名；僅在能明確辨識時填寫、不要臆測，無法辨識時回空字串）、description＝用一到兩句繁體中文描述此畫面的應用主題或情境（例如遊戲類型、專業領域或內容性質），供之後的英文翻譯作為參考。";

    private object BuildDescribePayload(string dataUrl) => new
    {
        model = _model,
        messages = new object[]
        {
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = DescribePrompt },
                    new { type = "image_url", image_url = new { url = dataUrl } },
                },
            },
        },
        response_format = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "image_context",
                strict = true,
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        description = new { type = "string" },
                    },
                    required = new[] { "name", "description" },
                    additionalProperties = false,
                },
            },
        },
    };

    private object BuildPayload(string dataUrl, bool pointMode = false)
    {
        // 智能配色（Issue #55）：有配色規則時 schema 多一 color 欄（色名）；無規則則維持原三欄（回歸保護）。
        var withColor = !string.IsNullOrWhiteSpace(_colorRules);
        var properties = new Dictionary<string, object>
        {
            ["original"] = new { type = "string" },
            ["phonetic"] = new { type = "string" },
            ["translation"] = new { type = "string" },
        };
        var required = new List<string> { "original", "phonetic", "translation" };
        if (withColor)
        {
            properties["color"] = new { type = "string" };
            required.Add("color");
        }
        return new
        {
            model = _model,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = BuildPrompt(_context, _colorRules, pointMode) },
                        new { type = "image_url", image_url = new { url = dataUrl } },
                    },
                },
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "screen_translation",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties,
                        required = required.ToArray(),
                        additionalProperties = false,
                    },
                },
            },
        };
    }

    /// <summary>解析 OpenAI 回應為三欄結果（internal 供單元測試）。缺欄/非 JSON 走 QueryException。</summary>
    internal static QueryResult Parse(string apiJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(apiJson);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new QueryException("API 回應為空。");
            }

            using var inner = JsonDocument.Parse(content);
            var r = inner.RootElement;
            if (!r.TryGetProperty("original", out var o)
                || !r.TryGetProperty("phonetic", out var p)
                || !r.TryGetProperty("translation", out var t))
            {
                throw new QueryException("回應格式錯誤：缺少欄位。");
            }
            // 智能配色（Issue #55；#189-checklist 改主題 hex）：color 欄選填,AI 回主題色 hex→調淡為白底可讀之筆記底色、非法/無則空
            var color = r.TryGetProperty("color", out var c) ? ColorMath.LightenForBackground(c.GetString()) : "";
            return new QueryResult(o.GetString() ?? "", p.GetString() ?? "", t.GetString() ?? "", color);
        }
        catch (QueryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new QueryException("回應解析失敗（格式錯誤）：" + ex.Message);
        }
    }

    /// <summary>自 OpenAI 回應取出 message.content 純文字（spec#9 圖片解釋；internal 供單元測試）。空/格式不符走 QueryException。</summary>
    internal static string ExtractContent(string apiJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(apiJson);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new QueryException("圖片解釋回應為空。");
            }
            return content.Trim();
        }
        catch (QueryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new QueryException("圖片解釋回應解析失敗：" + ex.Message);
        }
    }

    /// <summary>
    /// 解析圖片解釋之 structured content（{name, description}）為 <see cref="ImageContext"/>（#53；internal 供單元測試）。
    /// 容錯：若 content 非預期 JSON（模型偶未遵循 schema），退為 name 空、description＝整段文字，仍可用於情境描述。
    /// </summary>
    internal static ImageContext ParseImageContext(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var r = doc.RootElement;
            var name = r.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
            var desc = r.TryGetProperty("description", out var d) ? (d.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(desc)) // 無描述欄位視為非結構化回應，退整段為描述
            {
                return new ImageContext("", content.Trim());
            }
            return new ImageContext(name.Trim(), desc.Trim());
        }
        catch (JsonException)
        {
            return new ImageContext("", content.Trim()); // 非 JSON：整段當描述、名稱留空（不自動填名）
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
