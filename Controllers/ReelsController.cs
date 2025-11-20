using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BackendV2.Services;
using System;
using System.Threading.Tasks;
using BackendV2.DTOs;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Collections.Generic;

namespace BackendV2.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ReelsController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly IDbLogService _dbLog;
        private readonly ILogger<ReelsController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public ReelsController(IConfiguration configuration, IDbLogService dbLog, ILogger<ReelsController> logger, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _dbLog = dbLog;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        // Try to extract result/messages and return as an object (not a JSON string)
        private static bool TryBuildMinimalResult(string respText, out object minimal)
        {
            minimal = null!;
            if (string.IsNullOrWhiteSpace(respText)) return false;

            bool TryParse(string text, out object built)
            {
                built = null!;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var rEl))
                    {
                        var resultVal = rEl.GetString() ?? string.Empty;
                        string[] msgs = Array.Empty<string>();
                        if (root.TryGetProperty("messages", out var mEl) && mEl.ValueKind == JsonValueKind.Array)
                        {
                            var list = new List<string>();
                            foreach (var m in mEl.EnumerateArray())
                            {
                                if (m.ValueKind == JsonValueKind.String) list.Add(m.GetString() ?? string.Empty);
                                else list.Add(m.ToString() ?? string.Empty);
                            }
                            msgs = list.ToArray();
                        }
                        built = new { result = resultVal, messages = msgs };
                        return true;
                    }

                    // If root is string containing JSON, parse inner
                    if (root.ValueKind == JsonValueKind.String)
                    {
                        var inner = root.GetString();
                        if (!string.IsNullOrWhiteSpace(inner)) return TryParse(inner, out built);
                    }
                }
                catch
                {
                    // ignore parse errors
                }
                return false;
            }

            if (TryParse(respText, out var b)) { minimal = b; return true; }

            try
            {
                var start = respText.IndexOf('{');
                var end = respText.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    var sub = respText.Substring(start, end - start + 1);
                    if (TryParse(sub, out var b2)) { minimal = b2; return true; }
                }
            }
            catch { }

            return false;
        }

        [HttpPost("events")]
        public async Task<IActionResult> CreateReelEvent([FromBody] CreateReelEventDto dto)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs))
            {
                _logger.LogError("DefaultConnection string is not configured.");
                return StatusCode(500, "Internal server error: Database connection not configured.");
            }

            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync();

            using var transaction = (SqlTransaction)await conn.BeginTransactionAsync();

            string json = string.Empty;

            try
            {
                dto.MessageId = Guid.NewGuid();

                var eventCmd = new SqlCommand("CSP_ZANCANER_REELS_EVENTS_CREATE", conn, transaction);
                eventCmd.CommandType = CommandType.StoredProcedure;
                eventCmd.Parameters.AddWithValue("@MESSAGEID", dto.MessageId);
                eventCmd.Parameters.AddWithValue("@PRODUCTIONORDER", dto.ProductionOrder);
                eventCmd.Parameters.AddWithValue("@CANTREELSEJESUP", dto.CantReelsEjeSup);
                eventCmd.Parameters.AddWithValue("@CANTREELSEJEINF", dto.CantReelsEjeInf);
                eventCmd.Parameters.AddWithValue("@REELLENGHT", dto.ReelLength);
                eventCmd.Parameters.AddWithValue("@ENDLOT", dto.EndLot);

                await eventCmd.ExecuteNonQueryAsync();

                foreach (var reelDetail in dto.Reels)
                {
                    var detailCmd = new SqlCommand("CSP_ZANCANER_REELS_EVENTS_DETAILS_CREATE", conn, transaction);
                    detailCmd.CommandType = CommandType.StoredProcedure;
                    detailCmd.Parameters.AddWithValue("@MESSAGEID", dto.MessageId);
                    detailCmd.Parameters.AddWithValue("@EJE", reelDetail.Eje);
                    detailCmd.Parameters.AddWithValue("@POS", reelDetail.Pos);
                    detailCmd.Parameters.AddWithValue("@PRODUCTCODE", reelDetail.ProductCode);
                    detailCmd.Parameters.AddWithValue("@MANUALEXIT", reelDetail.ManualExit);
                    detailCmd.Parameters.AddWithValue("@EDGETRIM", reelDetail.EdgeTrim);

                    await detailCmd.ExecuteNonQueryAsync();
                }

                var payload = new
                {
                    messageType = "CREATE_SET",
                    messageId = dto.MessageId.ToString(),
                    productionOrder = dto.ProductionOrder.ToString(),
                    reelsOnUpperShaft = dto.CantReelsEjeSup,
                    reelsOnLowerShaft = dto.CantReelsEjeInf,
                    reelsLenght = dto.ReelLength,
                    endOfLot = dto.EndLot ? 1 : 0,
                    records = dto.Reels.ConvertAll(r => new
                    {
                        slitterShaft = r.Eje,
                        manualExit = r.ManualExit ? 1 : 0,
                        productionCode = r.ProductCode,
                        edgeTrim = r.EdgeTrim
                    })
                };

                json = JsonSerializer.Serialize(payload);

                var client = _httpClientFactory.CreateClient();
                var request = new HttpRequestMessage(HttpMethod.Post, "http://93.41.138.207:88/ZncWebApi/Reels/CreateSet")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                HttpResponseMessage? response = null;
                try
                {
                    response = await client.SendAsync(request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error calling external CreateSet API for order {Order}", dto.ProductionOrder);
                    try { await _dbLog.LogAsync(dto.UserID, "CREATE_SET_EXTERNAL_FAILED", $"Production_Order={dto.ProductionOrder}", dto.MessageId, ex.ToString()); } catch { }
                    try { transaction.Rollback(); } catch { }
                    return StatusCode(502, $"Failed to call external CreateSet API: {ex.Message}");
                }

                using (response)
                {
                    var respText = string.Empty;
                    try
                    {
                        respText = await response.Content.ReadAsStringAsync();
                    }
                    catch { }

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogError("External CreateSet API returned status {Status} for order {Order}. Response: {Response}", response.StatusCode, dto.ProductionOrder, respText);
                        try { await _dbLog.LogAsync(dto.UserID, "CREATE_SET_EXTERNAL_FAILED", $"Production_Order={dto.ProductionOrder}", dto.MessageId, respText); } catch { }
                        try { transaction.Rollback(); } catch { }

                        if (TryBuildMinimalResult(respText, out var minimalObj))
                        {
                            return StatusCode(502, new { error = "External CreateSet failed", body = minimalObj });
                        }

                        return StatusCode(502, new { error = "External CreateSet failed", status = (int)response.StatusCode, body = respText });
                    }

                    try
                    {
                        using var parsed = JsonDocument.Parse(respText);
                        if (parsed.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("result", out var resultEl))
                        {
                            var resultStr = resultEl.GetString();
                            if (!string.Equals(resultStr, "OK", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.LogError("External CreateSet returned logical failure for order {Order}. Body: {Body}", dto.ProductionOrder, respText);
                                try { await _dbLog.LogAsync(dto.UserID, "CREATE_SET_EXTERNAL_FAILED", $"Production_Order={dto.ProductionOrder}", dto.MessageId, respText); } catch { }
                                try { transaction.Rollback(); } catch { }

                                if (TryBuildMinimalResult(respText, out var minimalObj))
                                {
                                    return StatusCode(502, new { error = "External CreateSet returned non-OK result", body = minimalObj });
                                }

                                return StatusCode(502, new { error = "External CreateSet returned non-OK result", body = respText });
                            }
                        }
                    }
                    catch (JsonException) { }

                    try { await _dbLog.LogAsync(dto.UserID, "CREATE_SET_EXTERNAL", $"Production_Order={dto.ProductionOrder}", dto.MessageId, respText); } catch { }
                }

                await transaction.CommitAsync();
                try { await _dbLog.LogAsync(dto.UserID, "CREATE_REEL_EVENT", $"Production_Order={dto.ProductionOrder}", dto.MessageId, string.Empty); } catch { }

                _logger.LogInformation("Reel event created successfully for order {ProductionOrder} with MessageId {MessageId} and sent to external API.", dto.ProductionOrder, dto.MessageId);
                return Ok(new { MessageId = dto.MessageId, ProductionOrder = dto.ProductionOrder });
            }
            catch (SqlException sqlEx)
            {
                _logger.LogError(sqlEx, "SQL error creating reel event for order {ProductionOrder}. Rolling back transaction.", dto.ProductionOrder);
                try { await _dbLog.LogAsync(dto.UserID, "CREATE_REEL_EVENT_DB_FAILED", $"Production_Order={dto.ProductionOrder}", dto.MessageId, sqlEx.ToString()); } catch { }
                try { transaction.Rollback(); } catch { }

                return StatusCode(500, new { error = "Database error occurred while creating the reel event.", message = sqlEx.Message, sqlErrorNumber = sqlEx.Number, rollback = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating reel event for order {ProductionOrder}. Rolling back transaction.", dto.ProductionOrder);
                try { await _dbLog.LogAsync(dto.UserID, "CREATE_REEL_EVENT_FAILED", $"Production_Order={dto.ProductionOrder}", dto.MessageId, ex.ToString()); } catch { }
                try { transaction.Rollback(); } catch { }

                return StatusCode(500, new { error = "An error occurred while creating the reel event.", message = ex.Message, rollback = true });
            }
        }
    }
}
