using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Collections.Generic;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;

namespace BackendV2.Services
{
    // A recurrent background service that calls a stored procedure each interval and forwards results to external API
    public class RecurringHostedService : BackgroundService
    {
        private readonly ILogger<RecurringHostedService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServiceScopeFactory _scopeFactory;

        public RecurringHostedService(
            ILogger<RecurringHostedService> logger,
            IConfiguration configuration,
            IServiceScopeFactory scopeFactory,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _configuration = configuration;
            _scopeFactory = scopeFactory;
            _httpClientFactory = httpClientFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var intervalSeconds = _configuration.GetValue<int?>("RecurringService:IntervalSeconds") ?? 60;
            if (intervalSeconds < 5) intervalSeconds = 5; // minimum safe interval

            var procName = _configuration.GetValue<string>("RecurringService:ProcedureName") ?? "CSP_ZANCANER_ORDERS_SENT_DATA_TO_ZANCANCER_API";
            var externalEndpoint = _configuration.GetValue<string>("RecurringService:ExternalEndpoint") ?? "http://93.41.138.207:88/ZncWebApi/ProductionOrder/CreateOrder";
            var httpClientTimeoutSeconds = _configuration.GetValue<int?>("RecurringService:HttpTimeoutSeconds") ?? 30;

            _logger.LogInformation("RecurringHostedService started. Interval: {Interval}s, Procedure: {Proc}, Endpoint: {Endpoint}", intervalSeconds, procName, externalEndpoint);

            while (!stoppingToken.IsCancellationRequested)
            {
                var iterationStart = DateTime.UtcNow;
                var processedCount = 0;
                var processCorrelation = Guid.NewGuid();

                try
                {
                    // Log start of recurring process in bitacora
                    try
                    {
                        using var startScope = _scopeFactory.CreateScope();
                        var dbLogStart = startScope.ServiceProvider.GetService<IDbLogService>();
                        if (dbLogStart != null)
                        {
                            await dbLogStart.LogAsync(0, "RECURRING_SERVICE_START", $"Started at {iterationStart:o}", processCorrelation, string.Empty);
                        }
                    }
                    catch (Exception startLogEx)
                    {
                        _logger.LogWarning(startLogEx, "Failed to write RECURRING_SERVICE_START to DB log");
                    }

                    // Execute stored procedure and build records
                    var cs = _configuration.GetConnectionString("DefaultConnection");
                    if (string.IsNullOrWhiteSpace(cs))
                    {
                        _logger.LogWarning("DefaultConnection is not configured. Skipping proc execution.");
                    }
                    else
                    {
                        await using var conn = new SqlConnection(cs);
                        await using var cmd = new SqlCommand(procName, conn) { CommandType = CommandType.StoredProcedure };

                        await conn.OpenAsync(stoppingToken);

                        await using var reader = await cmd.ExecuteReaderAsync(stoppingToken);

                        var client = _httpClientFactory.CreateClient();
                        client.Timeout = TimeSpan.FromSeconds(httpClientTimeoutSeconds);

                        while (await reader.ReadAsync(stoppingToken))
                        {
                            processedCount++;

                            // Build single record payload from this row
                            var record = new Dictionary<string, object?>();
                            Guid? rowCorrelation = null;
                            string? productionOrderStr = null;
                            int productionOrderInt = 0;

                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                var name = reader.GetName(i);
                                var val = reader.IsDBNull(i) ? null : reader.GetValue(i);

                                if (string.Equals(name, "CorrelationId", StringComparison.OrdinalIgnoreCase) && val != null)
                                {
                                    if (Guid.TryParse(val.ToString(), out var g)) rowCorrelation = g;
                                    continue;
                                }

                                if (string.Equals(name, "productionOrder", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "ProductionOrder", StringComparison.OrdinalIgnoreCase))
                                {
                                    productionOrderStr = val?.ToString();
                                    record["productionOrder"] = productionOrderStr;
                                    if (int.TryParse(productionOrderStr, out var ord)) productionOrderInt = ord;
                                    continue;
                                }

                                // use camelCase key
                                var camelKey = Char.ToLowerInvariant(name[0]) + name.Substring(1);
                                record[camelKey] = val;
                            }

                            var messageId = rowCorrelation?.ToString() ?? Guid.NewGuid().ToString();

                            var payload = new Dictionary<string, object?>
                            {
                                ["messageType"] = "CREATE_ORDER",
                                ["messageId"] = messageId,
                                ["records"] = new List<Dictionary<string, object?>> { record }
                            };

                            var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = null, WriteIndented = false };
                            var json = JsonSerializer.Serialize(payload, jsonOptions);

                            // Build a CURL command representation for debugging / bitacora
                            var curlBody = json.Replace("'", "\\'");
                            var curlCommand = $"curl -X 'POST' '{externalEndpoint}' -H 'accept: application/json' -H 'Content-Type: application/json' -d '{curlBody}'";
                            _logger.LogInformation("Outgoing CURL: {Curl}", curlCommand);

                             using var content = new StringContent(json);
                             content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                            HttpResponseMessage? response = null;
                            var sendSuccess = false;
                            string errorMsg = string.Empty;

                            try
                            {
                                response = await client.PostAsync(externalEndpoint, content, stoppingToken);
                                if (response.IsSuccessStatusCode)
                                {
                                    // Read response body to detect logical errors even when HTTP 200
                                    var respText = await response.Content.ReadAsStringAsync(stoppingToken);
                                    if (!string.IsNullOrWhiteSpace(respText))
                                    {
                                        try
                                        {
                                            using var doc = JsonDocument.Parse(respText);
                                            if (doc.RootElement.TryGetProperty("result", out var resultEl) && resultEl.GetString() == "ERROR")
                                            {
                                                // Extract messages array if present and persist as errorMsg
                                                if (doc.RootElement.TryGetProperty("messages", out var messagesEl) && messagesEl.ValueKind == JsonValueKind.Array)
                                                {
                                                    var msgs = new List<string>();
                                                    foreach (var m in messagesEl.EnumerateArray())
                                                    {
                                                        if (m.ValueKind == JsonValueKind.String)
                                                        {
                                                            var t = m.GetString();
                                                            if (!string.IsNullOrWhiteSpace(t)) msgs.Add(t.Trim());
                                                        }
                                                    }

                                                    if (msgs.Count > 0)
                                                    {
                                                        errorMsg = string.Join(" | ", msgs);
                                                        sendSuccess = false;
                                                        _logger.LogWarning("External API returned logical ERROR for order {Order}: {ErrorMsg}", productionOrderStr, errorMsg);
                                                    }
                                                    else
                                                    {
                                                        // No messages array content
                                                        errorMsg = respText;
                                                        sendSuccess = false;
                                                        _logger.LogWarning("External API returned logical ERROR for order {Order}. Response: {Response}", productionOrderStr, respText);
                                                    }
                                                }
                                                else
                                                {
                                                    // result==ERROR but no messages array
                                                    errorMsg = respText;
                                                    sendSuccess = false;
                                                    _logger.LogWarning("External API returned logical ERROR for order {Order}. Response: {Response}", productionOrderStr, respText);
                                                }

                                                // do not treat as HTTP success for downstream
                                            }
                                            else
                                            {
                                                sendSuccess = true;
                                                _logger.LogInformation("Successfully sent order {Order} (messageId={MessageId})", productionOrderStr, messageId);
                                            }
                                        }
                                        catch (JsonException jex)
                                        {
                                            // Unable to parse JSON, treat as success but keep raw response if any
                                            sendSuccess = true;
                                            _logger.LogInformation(jex, "Sent order {Order} but failed to parse response JSON", productionOrderStr);
                                        }
                                    }
                                    else
                                    {
                                        sendSuccess = true;
                                        _logger.LogInformation("Successfully sent order {Order} (messageId={MessageId})", productionOrderStr, messageId);
                                    }
                                }
                                else
                                {
                                    var respText = await response.Content.ReadAsStringAsync(stoppingToken);
                                    errorMsg = respText;
                                    _logger.LogError("Failed to send order {Order}. StatusCode: {StatusCode}. Response: {Response}", productionOrderStr, response.StatusCode, respText);
                                }
                            }
                            catch (Exception ex)
                            {
                                errorMsg = ex.ToString();
                                _logger.LogError(ex, "Exception while sending order {Order}", productionOrderStr);
                            }
                            finally
                            {
                                response?.Dispose();
                            }

                            // Write RECURRING_SERVICE_SEND log per order and update status SP accordingly
                            try
                            {
                                using var scope = _scopeFactory.CreateScope();
                                var dbLog = scope.ServiceProvider.GetService<IDbLogService>();

                                if (dbLog != null)
                                {
                                    var paramText = $"Production_Order={productionOrderStr}";
                                    // If there was no error message, persist the curl command so we can reproduce the request
                                    if (string.IsNullOrWhiteSpace(errorMsg)) errorMsg = curlCommand;
                                    await dbLog.LogAsync(0, "RECURRING_SERVICE_SEND", paramText, Guid.Parse(messageId), errorMsg ?? string.Empty);
                                }
                                else
                                {
                                    _logger.LogWarning("IDbLogService not available in scope to write send bitacora for order {Order}.", productionOrderStr);
                                }
                            }
                            catch (Exception dbLogEx)
                            {
                                // Append DB log exception to errorMsg so it can be persisted or inspected
                                try
                                {
                                    var dbErr = dbLogEx.ToString();
                                    if (string.IsNullOrWhiteSpace(errorMsg)) errorMsg = dbErr; else errorMsg = errorMsg + " | DBLOG_ERROR: " + dbErr;
                                }
                                catch
                                {
                                    // ignore any failure when appending
                                }

                                _logger.LogWarning(dbLogEx, "Failed to write RECURRING_SERVICE_SEND to DB log for order {Order}", productionOrderStr);
                            }

                            // Update order status in local DB depending on success
                            try
                            {
                                if (productionOrderInt > 0)
                                {
                                    var newStatus = sendSuccess ? 900 : 200;
                                    await using var updConn = new SqlConnection(cs);
                                    await using var updCmd = new SqlCommand("CSP_ZANCANER_ORDERS_UPDATE_STATUS_BY_ORDER", updConn) { CommandType = CommandType.StoredProcedure };
                                    updCmd.Parameters.AddWithValue("@NEW_STATUS", newStatus);
                                    updCmd.Parameters.AddWithValue("@ORDER", productionOrderInt);

                                    await updConn.OpenAsync(stoppingToken);
                                    await updCmd.ExecuteNonQueryAsync(stoppingToken);

                                    _logger.LogInformation("Updated order {Order} status to {Status}", productionOrderInt, newStatus);
                                }
                                else
                                {
                                    _logger.LogWarning("Production order not parsed as int. Skipping status update for order {OrderStr}", productionOrderStr);
                                }
                            }
                            catch (Exception updEx)
                            {
                                _logger.LogError(updEx, "Failed to update status for order {Order}", productionOrderStr);
                            }
                        }

                        // end using reader/conn
                    }

                    // Log end of recurring process in bitacora
                    try
                    {
                        var endTime = DateTime.UtcNow;
                        using var endScope = _scopeFactory.CreateScope();
                        var dbLogEnd = endScope.ServiceProvider.GetService<IDbLogService>();
                        if (dbLogEnd != null)
                        {
                            await dbLogEnd.LogAsync(0, "RECURRING_SERVICE_END", $"Ended at {endTime:o}; Processed={processedCount}", processCorrelation, string.Empty);
                        }
                    }
                    catch (Exception endLogEx)
                    {
                        _logger.LogWarning(endLogEx, "Failed to write RECURRING_SERVICE_END to DB log");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // graceful shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred in recurring service iteration");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // shutting down
                }
            }

            _logger.LogInformation("RecurringHostedService stopping.");
        }
    }
}
