using Microsoft.AspNetCore.Mvc;
using BackendV2.Models;
using System.Data;
using Microsoft.Data.SqlClient;
using BackendV2.DTOs;
using BackendV2.Services;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BackendV2.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OrdenesController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly IDbLogService _dbLog;
        private readonly ILogger<OrdenesController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public OrdenesController(IConfiguration configuration, IDbLogService dbLog, ILogger<OrdenesController> logger, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _dbLog = dbLog;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        // GET api/ordenes
        [HttpGet]
        public async Task<ActionResult<IEnumerable<Orden>>> GetAll()
        {
            var connectionString = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                _logger.LogWarning("DefaultConnection not configured.");
                return StatusCode(500, "Connection string 'DefaultConnection' is not configured.");
            }

            var ordenes = new List<Orden>();

            try
            {
                await using var conn = new SqlConnection(connectionString);
                await using var cmd = new SqlCommand("CSP_ZANCANER_ORDERS_GET_ALL_RECORDS", conn)
                {
                    CommandType = CommandType.StoredProcedure
                };

                await conn.OpenAsync();
                await using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var orden = new Orden
                    {
                        ProductionOrder = reader["ProductionOrder"] != DBNull.Value ? Convert.ToInt32(reader["ProductionOrder"]) : 0,
                        SLITTER = reader["SLITTER"]?.ToString() ?? string.Empty,
                        CREATORUSER = reader["CREATORUSER"] != DBNull.Value ? Convert.ToInt32(reader["CREATORUSER"]) : 0,
                        CreateDateTime = reader["CreateDateTime"] != DBNull.Value ? Convert.ToDateTime(reader["CreateDateTime"]) : DateTime.UtcNow,
                        LastModificatorUser = reader["LastModificatorUser"] != DBNull.Value ? Convert.ToInt32(reader["LastModificatorUser"]) : 0,
                        ModificationDatetime = reader["ModificationDatetime"] != DBNull.Value ? Convert.ToDateTime(reader["ModificationDatetime"]) : DateTime.UtcNow,
                        StatusId = reader["STATUS_ID"] != DBNull.Value ? Convert.ToInt32(reader["STATUS_ID"]) : 0,
                        STATUS = reader["STATUS"]?.ToString() ?? string.Empty
                    };

                    ordenes.Add(orden);
                }

                return Ok(ordenes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving orders");
                return StatusCode(500, $"Error retrieving orders: {ex.Message}");
            }
        }

        // GET api/ordenes/{productionOrder} - read from DB
        [HttpGet("{productionOrder}")]
        public async Task<ActionResult<Orden>> Get(int productionOrder)
        {
            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs))
            {
                _logger.LogWarning("DefaultConnection not configured.");
                return StatusCode(500, "Connection string not configured.");
            }

            try
            {
                await using var conn = new SqlConnection(cs);
                await using var cmd = new SqlCommand("CSP_ZANCANER_ORDERS_GET_BY_ID", conn)
                {
                    CommandType = CommandType.StoredProcedure
                };

                cmd.Parameters.AddWithValue("@ProductionOrder", productionOrder);

                await conn.OpenAsync();
                await using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var orden = new Orden
                    {
                        ProductionOrder = reader["ProductionOrder"] != DBNull.Value ? Convert.ToInt32(reader["ProductionOrder"]) : 0,
                        SLITTER = reader["SLITTER"]?.ToString() ?? string.Empty,
                        CREATORUSER = reader["CREATORUSER"] != DBNull.Value ? Convert.ToInt32(reader["CREATORUSER"]) : 0,
                        CreateDateTime = reader["CreateDateTime"] != DBNull.Value ? Convert.ToDateTime(reader["CreateDateTime"]) : DateTime.UtcNow,
                        LastModificatorUser = reader["LastModificatorUser"] != DBNull.Value ? Convert.ToInt32(reader["LastModificatorUser"]) : 0,
                        ModificationDatetime = reader["ModificationDatetime"] != DBNull.Value ? Convert.ToDateTime(reader["ModificationDatetime"]) : DateTime.UtcNow,
                        STATUS = reader["STATUS"]?.ToString() ?? string.Empty
                    };

                    return Ok(orden);
                }

                return NotFound();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving order");
                return StatusCode(500, $"Error retrieving order: {ex.Message}");
            }
        }

        // POST -> call stored procedure to create order and return 201 with location
        [HttpPost]
        public async Task<ActionResult<Orden>> Create([FromBody] CreateOrderSpDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            // Validate ORDER provided and range
            if (!dto.ORDER.HasValue)
            {
                ModelState.AddModelError("ORDER", "ORDER is required.");
                return BadRequest(ModelState);
            }

            var orderValue = dto.ORDER.Value;
            if (orderValue <= 50000 || orderValue >= 1000000)
            {
                ModelState.AddModelError("ORDER", "ORDER must be greater than 50000 and less than 1000000.");
                return BadRequest(ModelState);
            }

            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs))
            {
                _logger.LogWarning("DefaultConnection not configured.");
                return StatusCode(500, "Connection string not configured.");
            }

            // Generate correlation id server-side
            var correlation = Guid.NewGuid();

            // Check external system: ensure order does not already exist remotely
            try
            {
                if (dto.ORDER.HasValue)
                {
                    var client = _httpClientFactory.CreateClient();
                    var getEndpoint = $"http://93.41.138.207:88/ZncWebApi/ProductionOrder/GetOrder/{dto.ORDER.Value}";
                    try
                    {
                        var getResp = await client.GetAsync(getEndpoint);
                        if (getResp.IsSuccessStatusCode)
                        {
                            // Remote order exists -> abort
                            // Try to mark local order status to 201
                            try
                            {
                                await using var updConn = new SqlConnection(cs);
                                await using var updCmd = new SqlCommand("CSP_ZANCANER_ORDERS_UPDATE_STATUS_BY_ORDER", updConn) { CommandType = CommandType.StoredProcedure };
                                updCmd.Parameters.AddWithValue("@NEW_STATUS", 201);
                                updCmd.Parameters.AddWithValue("@ORDER", dto.ORDER.Value);
                                await updConn.OpenAsync();
                                await updCmd.ExecuteNonQueryAsync();
                            }
                            catch (Exception updEx)
                            {
                                _logger.LogWarning(updEx, "Failed to update local status to 201 for order {Order}", dto.ORDER.Value);
                            }

                            return BadRequest("La order que desea crear ya existe en el ambiente de Zancaner");
                        }
                        else if (getResp.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            // not found -> proceed
                        }
                        else
                        {
                            var body = await getResp.Content.ReadAsStringAsync();
                            _logger.LogWarning("GetOrder returned {Status} for order {Order}. Response: {Resp}", getResp.StatusCode, dto.ORDER.Value, body);
                        }
                    }
                    catch (Exception exGet)
                    {
                        _logger.LogWarning(exGet, "Failed to call external GetOrder for order {Order}. Proceeding to create locally.", dto.ORDER.Value);
                    }
                }
            }
            catch (Exception exCheck)
            {
                _logger.LogWarning(exCheck, "Unexpected error while verifying external order existence");
            }

            try
            {
                await using var conn = new SqlConnection(cs);
                await using var cmd = new SqlCommand("CSP_ZANCANER_ORDERS_CREATE_ORDER", conn)
                {
                    CommandType = CommandType.StoredProcedure
                };

                // Pass @ORDER as input only (the SP expects it as input)
                cmd.Parameters.AddWithValue("@USERID", dto.USERID);
                cmd.Parameters.AddWithValue("@ORDER", orderValue);
                cmd.Parameters.AddWithValue("@CorrelationId", correlation);

                await conn.OpenAsync();

                // SP does a final SELECT @ORDER; use ExecuteScalarAsync to read it
                var scalar = await cmd.ExecuteScalarAsync();
                var createdOrderNumber = scalar != null && scalar != DBNull.Value ? Convert.ToInt32(scalar) : 0;

                // Log success in bitácora (non-blocking)
                try
                {
                    await _dbLog.LogAsync(dto.USERID, "CREATE_MANUAL_ORDER", $"Production_Order={createdOrderNumber}", correlation, string.Empty);
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, "Failed to write to DB bitacora from Create");
                }

                var orden = new Orden
                {
                    ProductionOrder = createdOrderNumber,
                    SLITTER = string.Empty,
                    CREATORUSER = dto.USERID,
                    CreateDateTime = DateTime.UtcNow,
                    LastModificatorUser = dto.USERID,
                    ModificationDatetime = DateTime.UtcNow,
                    STATUS = string.Empty
                };

                return CreatedAtAction(nameof(Get), new { productionOrder = orden.ProductionOrder }, orden);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating order");

                // Try to log error, ignore logging failures
                try
                {
                    await _dbLog.LogAsync(dto.USERID, "CREATE_MANUAL_ORDER", $"Production_Order={dto.ORDER}", correlation, ex.ToString());
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, "Failed to write error to DB bitacora");
                }

                return StatusCode(500, $"Error creating order: {ex.Message}");
            }
        }

        // New endpoint: ValidaStartOrder - checks if an order exists by calling CSP_ZANCANER_ORDERS_GET_BY_ID
        [HttpGet("valida/{productionOrder}")]
        public async Task<IActionResult> ValidaStartOrder(int productionOrder)
        {
            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs))
            {
                _logger.LogWarning("DefaultConnection not configured.");
                return StatusCode(500, "Connection string not configured.");
            }

            try
            {
                await using var conn = new SqlConnection(cs);
                await using var cmd = new SqlCommand("CSP_ZANCANER_ORDERS_GET_BY_ID", conn)
                {
                    CommandType = CommandType.StoredProcedure
                };

                cmd.Parameters.AddWithValue("@ORDER", productionOrder);

                await conn.OpenAsync();
                await using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    // Record exists
                    return Ok(new { exists = true });
                }

                // Not found
                return Ok(new { exists = false });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating start order");
                return StatusCode(500, $"Error validating start order: {ex.Message}");
            }
        }

        // PUT -> call stored procedure to update
        [HttpPut("{productionOrder}")]
        public async Task<IActionResult> Update(int productionOrder, [FromBody] Orden update)
        {
            if (update == null) return BadRequest();

            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs)) return StatusCode(500, "Connection string not configured.");

            try
            {
                await using var conn = new SqlConnection(cs);
                await using var cmd = new SqlCommand("CSP_ZANCANER_ORDERS_UPDATE_ORDER", conn)
                {
                    CommandType = CommandType.StoredProcedure
                };

                cmd.Parameters.AddWithValue("@ORDER", productionOrder);
                cmd.Parameters.AddWithValue("@SLITTER", update.SLITTER ?? string.Empty);
                cmd.Parameters.AddWithValue("@LastModificatorUser", update.LastModificatorUser);
                cmd.Parameters.AddWithValue("@ModificationDatetime", update.ModificationDatetime);
                cmd.Parameters.AddWithValue("@Status", update.STATUS ?? string.Empty);

                await conn.OpenAsync();
                var rows = await cmd.ExecuteNonQueryAsync();

                if (rows == 0) return NotFound();
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating order");
                return StatusCode(500, $"Error updating order: {ex.Message}");
            }
        }

        // DELETE -> call stored procedure to delete
        [HttpDelete("{productionOrder}")]
        public async Task<IActionResult> Delete(int productionOrder)
        {
            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs)) return StatusCode(500, "Connection string not configured.");

            // correlation/messageId will be fetched from DB
            string? messageId = null;
            int statusValue = 0;

            try
            {
                // First, get the record (and its correlation/messageId and STATUS) using existing SP
                await using (var conn = new SqlConnection(cs))
                await using (var cmd = new SqlCommand("CSP_ZANCANER_ORDERS_GET_BY_ID", conn) { CommandType = CommandType.StoredProcedure })
                {
                    cmd.Parameters.AddWithValue("@ORDER", productionOrder);
                    await conn.OpenAsync();
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound("La orden seleccionada para eliminar no ha sido encontrada");
                    }

                    // Extract STATUS if present
                    try
                    {
                        var statusObj = reader["STATUS"];
                        var statusStr = statusObj != DBNull.Value ? statusObj?.ToString() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrWhiteSpace(statusStr) && int.TryParse(statusStr, out var parsed)) statusValue = parsed;
                    }
                    catch
                    {
                        // ignore - default statusValue = 0
                    }

                    // If status indicates already deleted (>1000), return conflict with message
                    if (statusValue > 1000)
                    {
                        return Conflict($"La orden {productionOrder} ya ha sido eliminada previamente. No es posible eliminar");
                    }

                    // Try to find a correlation/messageId column in the returned row
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var colName = reader.GetName(i)?.ToLowerInvariant() ?? string.Empty;
                        if (colName.Contains("correl") || colName.Contains("messageid") || colName.Contains("message_id") || colName.Contains("message"))
                        {
                            var val = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                            if (!string.IsNullOrWhiteSpace(val))
                            {
                                messageId = val;
                                break;
                            }
                        }
                    }
                }

                // If no messageId returned, generate one
                if (string.IsNullOrWhiteSpace(messageId)) messageId = Guid.NewGuid().ToString();

                // Decide whether to call external API depending on STATUS
                var shouldCallExternal = statusValue >= 900 && statusValue <= 999;

                if (shouldCallExternal)
                {
                    // Prepare payload for external DELETE API
                    var payload = new
                    {
                        messageType = "DELETE_ORDER",
                        messageId = messageId,
                        productionOrder = productionOrder.ToString()
                    };

                    var json = JsonSerializer.Serialize(payload);

                    var client = _httpClientFactory.CreateClient();
                    var request = new HttpRequestMessage(HttpMethod.Delete, "http://93.41.138.207:88/ZncWebApi/ProductionOrder/DeleteOrder")
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json")
                    };
                    request.Headers.Accept.Clear();
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    HttpResponseMessage? response = null;
                    try
                    {
                        response = await client.SendAsync(request);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error calling external delete API for order {Order}", productionOrder);
                        // Log failure to bitacora and return 502
                        try
                        {
                            await _dbLog.LogAsync(0, "DELETE_ORDER_EXTERNAL", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), ex.ToString());
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogError(logEx, "Failed to write error to DB bitacora from Delete external call");
                        }

                        return StatusCode(502, $"Failed to call external delete API: {ex.Message}");
                    }

                    using (response)
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            var respText = await response.Content.ReadAsStringAsync();
                            _logger.LogError("External delete API returned status {Status} for order {Order}. Response: {Response}", response.StatusCode, productionOrder, respText);

                            try
                            {
                                await _dbLog.LogAsync(0, "DELETE_ORDER_EXTERNAL", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), $"ExternalStatus={response.StatusCode};Response={respText}");
                            }
                            catch (Exception logEx)
                            {
                                _logger.LogError(logEx, "Failed to write error to DB bitacora from Delete external failure");
                            }

                            // If external returned 404 with JSON containing a 'messages' array, try to extract the "ERROR => ..." entry and return it
                            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                            {
                                try
                                {
                                    using var doc = JsonDocument.Parse(respText);
                                    if (doc.RootElement.TryGetProperty("messages", out var messagesEl) && messagesEl.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var m in messagesEl.EnumerateArray())
                                        {
                                            if (m.ValueKind == JsonValueKind.String)
                                            {
                                                var txt = m.GetString();
                                                if (!string.IsNullOrWhiteSpace(txt) && txt.Contains("ERROR =>"))
                                                {
                                                    // Return the specific error message from external API, prefixed with ZANCANER
                                                    return NotFound($"ZANCANER {txt}");
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception parseEx)
                                {
                                    _logger.LogWarning(parseEx, "Failed to parse external 404 response JSON for order {Order}", productionOrder);
                                }

                                // Fallback: return full response text with 404
                                return NotFound(respText);
                            }

                            return StatusCode(502, $"External delete failed: {response.StatusCode}");
                        }
                    }

                    // If external succeeded, proceed to delete below
                }

                // Proceed to delete locally via SP (either because external succeeded or because STATUS < 900)
                try
                {
                    await using var conn2 = new SqlConnection(cs);
                    await using var cmd2 = new SqlCommand("CSP_ZANCANER_ORDERS_DELETE_ORDER", conn2) { CommandType = CommandType.StoredProcedure };
                    cmd2.Parameters.AddWithValue("@ORDER", productionOrder);
                    await conn2.OpenAsync();
                    var rows = await cmd2.ExecuteNonQueryAsync();

                    if (rows == 0) return NotFound();

                    // Log success in bitácora
                    try
                    {
                        await _dbLog.LogAsync(0, "DELETE_ORDER", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), string.Empty);
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogError(logEx, "Failed to write to DB bitacora from Delete");
                    }

                    return NoContent();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting order after external success or direct delete");
                    try
                    {
                        await _dbLog.LogAsync(0, "DELETE_ORDER", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), ex.ToString());
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogError(logEx, "Failed to write error to DB bitacora from Delete (after external success)");
                    }

                    return StatusCode(500, $"Error deleting order: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in Delete for order {Order}", productionOrder);
                return StatusCode(500, $"Error deleting order: {ex.Message}");
            }
        }

        // Diagnostic endpoint to test DB bitácora writing
        [HttpGet("testlog")]
        public async Task<IActionResult> TestLog()
        {
            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs))
            {
                _logger.LogWarning("DefaultConnection not configured.");
                return StatusCode(500, "Connection string not configured.");
            }

            try
            {
                var correlation = Guid.NewGuid();
                await _dbLog.LogAsync(0, "TEST_LOG", "test-param", correlation, string.Empty);
                return Ok(new { success = true, correlation });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Test log failed");
                return StatusCode(500, $"Test log failed: {ex.Message}");
            }
        }

        // POST -> Start order: prepares data, validates, calls external StartOrder and updates status
        [HttpPost("start/{productionOrder}/{slitter}/{userId}")]
        public async Task<IActionResult> StartOrder(int productionOrder, int slitter, int userId)
        {
            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs)) return StatusCode(500, "Connection string not configured.");

            string? messageId = null;
            int statusValue = 0;

            try
            {
                // 0) Ensure order exists by calling CSP_ZANCANER_ORDERS_GET_BY_ID (@ORDER)
                try
                {
                    await using var existConn = new SqlConnection(cs);
                    await using var existCmd = new SqlCommand("CSP_ZANCANER_ORDERS_GET_BY_ID", existConn) { CommandType = CommandType.StoredProcedure };
                    existCmd.Parameters.AddWithValue("@ORDER", productionOrder);
                    await existConn.OpenAsync();
                    await using var existReader = await existCmd.ExecuteReaderAsync();
                    if (!await existReader.ReadAsync())
                    {
                        return NotFound("La orden de producción no fue encontrada");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error checking existence for order {Order}", productionOrder);
                    return StatusCode(500, $"Error checking order existence: {ex.Message}");
                }

                // 1) Call CSP_ZANCANER_ORDERS_DETAILS_CREATE_ORDER_DATA (@order)
                try
                {
                    await using var prepConn = new SqlConnection(cs);
                    await using var prepCmd = new SqlCommand("CSP_ZANCANER_ORDERS_DETAILS_CREATE_ORDER_DATA", prepConn) { CommandType = CommandType.StoredProcedure };
                    prepCmd.Parameters.AddWithValue("@PRODUCTION_ORDER", productionOrder);
                    await prepConn.OpenAsync();
                    await prepCmd.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing CSP_ZANCANER_ORDERS_DETAILS_CREATE_ORDER_DATA for order {Order}", productionOrder);
                    return StatusCode(500, $"Error preparing order data: {ex.Message}");
                }

                // 2) Call CSP_ZANCANER_ORDERS_VALIDA_DATOS_ORDEN_BY_ORDER (@order)
                try
                {
                    await using var valConn = new SqlConnection(cs);
                    await using var valCmd = new SqlCommand("CSP_ZANCANER_ORDERS_VALIDA_DATOS_ORDEN_BY_ORDER", valConn) { CommandType = CommandType.StoredProcedure };
                    valCmd.Parameters.AddWithValue("@ORDER", productionOrder);
                    await valConn.OpenAsync();
                    await valCmd.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing CSP_ZANCANER_ORDERS_VALIDA_DATOS_ORDEN_BY_ORDER for order {Order}", productionOrder);
                    return StatusCode(500, $"Error validating order data: {ex.Message}");
                }

                // 3) Call CSP_ZANCANER_ORDERS_GET_BY_ID to retrieve STATUS and CorrelationId
                await using (var conn = new SqlConnection(cs))
                await using (var cmd = new SqlCommand("CSP_ZANCANER_ORDERS_GET_BY_ID", conn) { CommandType = CommandType.StoredProcedure })
                {
                    cmd.Parameters.AddWithValue("@ORDER", productionOrder);
                    await conn.OpenAsync();
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound("La orden solicitada no ha sido encontrada");
                    }

                    // Extract STATUS
                    try
                    {
                        var statusObj = reader["STATUS"];
                        var statusStr = statusObj != DBNull.Value ? statusObj?.ToString() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrWhiteSpace(statusStr) && int.TryParse(statusStr, out var parsed)) statusValue = parsed;
                    }
                    catch { }

                    // If status not in required range, return error
                    if (!(statusValue >= 900 && statusValue <= 999))
                    {
                        return BadRequest("La orden no cumple el estado requerido para ser puesta en marcha.");
                    }

                    // find correlation/messageId
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var colName = reader.GetName(i)?.ToLowerInvariant() ?? string.Empty;
                        if (colName.Contains("correl") || colName.Contains("messageid") || colName.Contains("message_id") || colName.Contains("message"))
                        {
                            var val = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                            if (!string.IsNullOrWhiteSpace(val))
                            {
                                messageId = val;
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(messageId)) messageId = Guid.NewGuid().ToString();

                // 4) Call external StartOrder API
                var payload = new
                {
                    messageType = "START_ORDER",
                    messageId = messageId,
                    productionOrder = productionOrder.ToString(),
                    slitter = slitter
                };

                var json = JsonSerializer.Serialize(payload);
                var client = _httpClientFactory.CreateClient();
                var request = new HttpRequestMessage(HttpMethod.Post, "http://93.41.138.207:88/ZncWebApi/ProductionOrder/StartOrder")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                HttpResponseMessage? response = null;
                string externalStartRespBody = string.Empty;
                int externalStartStatus = 0;
                try
                {
                    response = await client.SendAsync(request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error calling external StartOrder API for order {Order}", productionOrder);
                    // update status to 901 on external failure
                    try
                    {
                        await using var updConnFail = new SqlConnection(cs);
                        await using var updCmdFail = new SqlCommand("CSP_ZANCANER_ORDERS_UPDATE_STATUS_BY_ORDER", updConnFail) { CommandType = CommandType.StoredProcedure };
                        updCmdFail.Parameters.AddWithValue("@NEW_STATUS", 901);
                        updCmdFail.Parameters.AddWithValue("@ORDER", productionOrder);
                        await updConnFail.OpenAsync();
                        await updCmdFail.ExecuteNonQueryAsync();
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogError(logEx, "Failed to update status to 901 after external StartOrder exception for order {Order}", productionOrder);
                    }

                    return StatusCode(502, $"Failed to call external StartOrder API: {ex.Message}");
                }

                using (response)
                {
                    // Read response body for logging/debugging (do this once)
                    var respText = string.Empty;
                    try
                    {
                        respText = await response.Content.ReadAsStringAsync();
                    }
                    catch (Exception readEx)
                    {
                        _logger.LogWarning(readEx, "Failed to read external StartOrder response body for order {Order}", productionOrder);
                    }

                    // Log status and body to console/log for debugging
                    _logger.LogInformation("External StartOrder response for order {Order}: Status={Status}, Body={Body}", productionOrder, (int)response.StatusCode, respText);

                    // capture for returning to caller (debug)
                    externalStartRespBody = respText;
                    externalStartStatus = (int)response.StatusCode;

                    // If HTTP 200 but payload indicates result != OK, treat as failure and return error with messages
                    try
                    {
                        using var parsed = JsonDocument.Parse(respText);
                        if (parsed.RootElement.TryGetProperty("result", out var resultEl))
                        {
                            var resultStr = resultEl.GetString();
                            if (!string.Equals(resultStr, "OK", StringComparison.OrdinalIgnoreCase))
                            {
                                // extract messages if any
                                string err = respText;
                                if (parsed.RootElement.TryGetProperty("messages", out var messagesEl) && messagesEl.ValueKind == JsonValueKind.Array)
                                {
                                    var msgs = new List<string>();
                                    foreach (var m in messagesEl.EnumerateArray())
                                    {
                                        if (m.ValueKind == JsonValueKind.String) msgs.Add(m.GetString() ?? string.Empty);
                                    }

                                    if (msgs.Count > 0) err = string.Join(" | ", msgs);
                                }

                                _logger.LogError("External StartOrder returned result={Result} for order {Order}. Messages: {Messages}", resultStr, productionOrder, err);

                                // update status to 901 on external non-success (logical failure)
                                try
                                {
                                    await using var updConnFail = new SqlConnection(cs);
                                    await using var updCmdFail = new SqlCommand("CSP_ZANCANER_ORDERS_UPDATE_STATUS_BY_ORDER", updConnFail) { CommandType = CommandType.StoredProcedure };
                                    updCmdFail.Parameters.AddWithValue("@NEW_STATUS", 901);
                                    updCmdFail.Parameters.AddWithValue("@ORDER", productionOrder);
                                    await updConnFail.OpenAsync();
                                    await updCmdFail.ExecuteNonQueryAsync();
                                }
                                catch (Exception logEx)
                                {
                                    _logger.LogError(logEx, "Failed to update status to 901 after external StartOrder logical failure for order {Order}", productionOrder);
                                }

                                // log to bitacora the external failure
                                try
                                {
                                    await _dbLog.LogAsync(userId, "START_ORDER_EXTERNAL", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), err);
                                }
                                catch (Exception logEx)
                                {
                                    _logger.LogWarning(logEx, "Failed to write START_ORDER_EXTERNAL failure to DB bitacora for order {Order}", productionOrder);
                                }

                                // Return BadRequest with the extracted error messages
                                return BadRequest(new { error = err });
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // ignore parse errors here; we'll treat based on HTTP status below
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        // respText already read above into variable; reuse it
                        _logger.LogError("External StartOrder API returned status {Status} for order {Order}. Response: {Response}", response.StatusCode, productionOrder, respText);

                        // update status to 901 on external non-success
                        try
                        {
                            await using var updConnFail = new SqlConnection(cs);
                            await using var updCmdFail = new SqlCommand("CSP_ZANCANER_ORDERS_UPDATE_STATUS_BY_ORDER", updConnFail) { CommandType = CommandType.StoredProcedure };
                            updCmdFail.Parameters.AddWithValue("@NEW_STATUS", 901);
                            updCmdFail.Parameters.AddWithValue("@ORDER", productionOrder);
                            await updConnFail.OpenAsync();
                            await updCmdFail.ExecuteNonQueryAsync();
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogError(logEx, "Failed to update status to 901 after external StartOrder failure for order {Order}", productionOrder);
                        }

                        return StatusCode(502, $"External StartOrder failed: {response.StatusCode}");
                    }
                }

                // External returned success -> update local status to 1300
                try
                {
                    await using var updConn = new SqlConnection(cs);
                    await using var updCmd = new SqlCommand("CSP_ZANCANER_ORDERS_UPDATE_STATUS_BY_ORDER", updConn) { CommandType = CommandType.StoredProcedure };
                    updCmd.Parameters.AddWithValue("@NEW_STATUS", 1300);
                    updCmd.Parameters.AddWithValue("@ORDER", productionOrder);
                    await updConn.OpenAsync();
                    await updCmd.ExecuteNonQueryAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update status to 1300 after external StartOrder success for order {Order}", productionOrder);
                    return StatusCode(500, $"Failed to update order status after external StartOrder: {ex.Message}");
                }

                // Log success in bitácora (use provided userId)
                try
                {
                    await _dbLog.LogAsync(userId, "START_ORDER", $"Production_Order={productionOrder};Slitter={slitter}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), string.Empty);
                }
                catch (Exception logEx)
                {
                    _logger.LogError(logEx, "Failed to write to DB bitacora from StartOrder for order {Order}", productionOrder);
                }

                // Return OK (no body) to indicate success
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in StartOrder for order {Order}", productionOrder);
                return StatusCode(500, $"Error starting order: {ex.Message}");
            }
        }

        // New endpoint: ValidaDatosOrden - validates production order data via stored procedure and logs result
        [HttpGet("validadatosorden/{NroOrden}")]
        public async Task<IActionResult> ValidaDatosOrden(int NroOrden)
        {
            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs))
            {
                _logger.LogWarning("DefaultConnection not configured.");
                return StatusCode(500, "Connection string not configured.");
            }

            try
            {
                int hasErrors = 0;
                string errorMsg = string.Empty;
                bool statusRefreshed = false;

                await using (var conn = new SqlConnection(cs))
                await using (var cmd = new SqlCommand("CSP_ZANCANER_ORDERS_VALIDA_DATOS_ORDEN_BY_ORDER", conn) { CommandType = CommandType.StoredProcedure })
                {
                    cmd.Parameters.AddWithValue("@ORDER", NroOrden);

                    await conn.OpenAsync();

                    // Assume SP returns a result set with HAS_ERRORS and ERROR_MSG columns
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (await reader.ReadAsync())
                    {
                        // Robustly inspect returned columns to find HAS_ERRORS and ERROR_MSG (case-insensitive, different names)
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            var colName = reader.GetName(i) ?? string.Empty;
                            var lower = colName.ToLowerInvariant();
                            object? val = reader.IsDBNull(i) ? null : reader.GetValue(i);

                            try
                            {
                                if (lower.Contains("has_errors") || lower.Contains("haserrors") || lower == "has_errors" || lower == "haserrors")
                                {
                                    if (val != null)
                                    {
                                        // Convert to int in a few possible boxed types
                                        try
                                        {
                                            hasErrors = Convert.ToInt32(val);
                                        }
                                        catch (Exception exConv)
                                        {
                                            _logger.LogWarning(exConv, "Unable to convert HAS_ERRORS value '{Val}' to int for order {Order}", val, NroOrden);
                                        }
                                    }
                                }

                                if (lower.Contains("error_msg") || lower.Contains("errormsg") || lower.Contains("error") )
                                {
                                    if (val != null)
                                    {
                                        errorMsg = val.ToString() ?? string.Empty;
                                    }
                                }

                                // New column: STATUS_REFRESHED (bit)
                                if (lower.Contains("status_refreshed") || lower.Contains("statusrefreshed") || lower == "status_refreshed" || lower == "statusrefreshed")
                                {
                                    if (val != null)
                                    {
                                        try
                                        {
                                            // handle common SQL bit representations
                                            if (val is bool b) statusRefreshed = b;
                                            else statusRefreshed = Convert.ToInt32(val) != 0;
                                        }
                                        catch (Exception exConv)
                                        {
                                            _logger.LogWarning(exConv, "Unable to convert STATUS_REFRESHED value '{Val}' to bool for order {Order}", val, NroOrden);
                                        }
                                    }
                                }
                            }
                            catch (Exception exCol)
                            {
                                _logger.LogWarning(exCol, "Error reading column {Col} from validation SP for order {Order}", colName, NroOrden);
                            }
                        }
                    }
                    else
                    {
                        _logger.LogInformation("CSP_ZANCANER_ORDERS_VALIDA_DATOS_ORDEN_BY_ORDER returned no rows for order {Order}", NroOrden);
                    }
                }

                // Write to bitacora: always record action and params; include errorMsg if hasErrors == 1
                try
                {
                    var correlation = Guid.NewGuid();
                    var paramText = $"Production_Order={NroOrden};StatusRefreshed={statusRefreshed}";
                    await _dbLog.LogAsync(0, "CSP_ZANCANER_ORDERS_VALIDA_DATOS_ORDEN_BY_ORDER", paramText, correlation, hasErrors == 1 ? errorMsg ?? string.Empty : string.Empty);
                }
                catch (Exception logEx)
                {
                    _logger.LogWarning(logEx, "Failed to write validation result to DB bitacora for order {Order}", NroOrden);
                }

                return Ok(new { hasErrors = hasErrors == 1, errorMsg, statusRefreshed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating order data for {Order}", NroOrden);
                return StatusCode(500, $"Error validating order data: {ex.Message}");
            }
        }

        // POST -> Stop order: calls external StopOrder, then local stop SP and updates status
        [HttpPost("stop/{productionOrder}/{slitter}/{userId}")]
        public async Task<IActionResult> StopOrder(int productionOrder, int slitter, int userId)
        {
            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs)) return StatusCode(500, "Connection string not configured.");

            string? messageId = null;

            try
            {
                // 1) Ensure order exists and try to extract correlation/messageId from DB
                await using (var conn = new SqlConnection(cs))
                await using (var cmd = new SqlCommand("CSP_ZANCANER_ORDERS_GET_BY_ID", conn) { CommandType = CommandType.StoredProcedure })
                {
                    cmd.Parameters.AddWithValue("@ORDER", productionOrder);
                    await conn.OpenAsync();
                    await using var reader = await cmd.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        return NotFound("Error, la orden a detener no existe");
                    }

                    // Try to find correlation/messageId column
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var colName = reader.GetName(i)?.ToLowerInvariant() ?? string.Empty;
                        if (colName.Contains("correl") || colName.Contains("messageid") || colName.Contains("message_id") || colName.Contains("message"))
                        {
                            var val = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                            if (!string.IsNullOrWhiteSpace(val))
                            {
                                messageId = val;
                                break;
                            }
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(messageId)) messageId = Guid.NewGuid().ToString();

                // 2) Call external StopOrder API
                var payload = new
                {
                    messageType = "STOP_ORDER",
                    messageId = messageId,
                    productionOrder = productionOrder.ToString(),
                    slitter = slitter
                };

                var json = JsonSerializer.Serialize(payload);
                var client = _httpClientFactory.CreateClient();
                var request = new HttpRequestMessage(HttpMethod.Post, "http://93.41.138.207:88/ZncWebApi/ProductionOrder/StopOrder")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Accept.Clear();
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                HttpResponseMessage? response = null;
                try
                {
                    response = await client.SendAsync(request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error calling external StopOrder API for order {Order}", productionOrder);
                    try
                    {
                        await _dbLog.LogAsync(userId, "STOP_ORDER_EXTERNAL", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), ex.ToString());
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogWarning(logEx, "Failed to write STOP_ORDER_EXTERNAL error to DB bitacora for order {Order}", productionOrder);
                    }

                    return StatusCode(502, $"Failed to call external StopOrder API: {ex.Message}");
                }

                using (response)
                {
                    var respText = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // parse messages array if present
                        string errorMsg = respText;
                        try
                        {
                            using var parsedDocNotFound = JsonDocument.Parse(respText);
                            if (parsedDocNotFound.RootElement.TryGetProperty("messages", out var messagesEl) && messagesEl.ValueKind == JsonValueKind.Array)
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

                                if (msgs.Count > 0) errorMsg = string.Join(" | ", msgs);

                                // If any message contains 'ERROR =>' prefix it with 'ZANCANER '
                                for (int i = 0; i < msgs.Count; i++)
                                {
                                    var single = msgs[i];
                                    if (!string.IsNullOrWhiteSpace(single) && single.IndexOf("ERROR =>", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        msgs[i] = "ZANCANER " + single;
                                        errorMsg = string.Join(" | ", msgs);
                                        break;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // ignore parse errors, keep raw text
                        }

                        // log to bitacora: attempted stop but not found
                        try
                        {
                            await _dbLog.LogAsync(userId, "STOP_ORDER_EXTERNAL", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), errorMsg);
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogWarning(logEx, "Failed to write STOP_ORDER_EXTERNAL (not found) to DB bitacora for order {Order}", productionOrder);
                        }

                        return NotFound(errorMsg);
                    }

                    // Handle non-OK result in a 200 response
                    if (response.IsSuccessStatusCode)
                    {
                        try
                        {
                            using var parsedDoc = JsonDocument.Parse(respText);
                            if (parsedDoc.RootElement.TryGetProperty("result", out var resultEl) &&
                                !string.Equals(resultEl.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
                            {
                                // External API returned a logical error (e.g., result: "ERROR")
                                var err = respText;
                                if (parsedDoc.RootElement.TryGetProperty("messages", out var messagesEl) && messagesEl.ValueKind == JsonValueKind.Array)
                                {
                                    var msgs = messagesEl.EnumerateArray().Select(m => m.GetString()).Where(s => !string.IsNullOrEmpty(s));
                                    err = string.Join(" | ", msgs);
                                }

                                _logger.LogWarning("External StopOrder for order {Order} returned logical error: {Error}", productionOrder, err);
                                try
                                {
                                    await _dbLog.LogAsync(userId, "STOP_ORDER_EXTERNAL_FAIL", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), err);
                                }
                                catch (Exception logEx)
                                {
                                    _logger.LogWarning(logEx, "Failed to write STOP_ORDER_EXTERNAL_FAIL to DB bitacora for order {Order}", productionOrder);
                                }

                                return BadRequest(new { error = err });
                            }
                        }
                        catch (JsonException jex)
                        {
                            _logger.LogWarning(jex, "Failed to parse successful external StopOrder response JSON for order {Order}. Body: {Body}", productionOrder, respText);
                            // Treat as failure if we can't parse a success response that should have a specific format
                            return StatusCode(502, "Failed to parse external StopOrder response.");
                        }
                    }
                    else // Handle non-success status codes other than 404
                    {
                        _logger.LogError("External StopOrder API for order {Order} returned status {Status}. Response: {Response}", productionOrder, response.StatusCode, respText);
                        try
                        {
                            await _dbLog.LogAsync(userId, "STOP_ORDER_EXTERNAL", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), $"ExternalStatus={response.StatusCode};Response={respText}");
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogWarning(logEx, "Failed to write STOP_ORDER_EXTERNAL failure to DB bitacora for order {Order}", productionOrder);
                        }

                        return StatusCode(502, $"External StopOrder failed: {response.StatusCode}");
                    }


                    // --- If we reach here, the external call was fully successful (HTTP 200 and result: "OK") ---

                    // Log external success info from messages if available
                    string externalInfo = string.Empty;
                    try
                    {
                        using var parsedDocSuccess = JsonDocument.Parse(respText);
                        if (parsedDocSuccess.RootElement.TryGetProperty("messages", out var messagesEl) && messagesEl.ValueKind == JsonValueKind.Array)
                        {
                            var msgs = messagesEl.EnumerateArray().Select(m => m.GetString()?.Trim()).Where(s => !string.IsNullOrWhiteSpace(s));
                            if (msgs.Any()) externalInfo = string.Join(" | ", msgs);
                        }
                    }
                    catch { /* Ignore parsing errors here, we already validated the important parts */ }

                    // record external success in bitacora
                    try
                    {
                        await _dbLog.LogAsync(userId, "STOP_ORDER_EXTERNAL", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), externalInfo);
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogWarning(logEx, "Failed to write STOP_ORDER_EXTERNAL success to DB bitacora for order {Order}", productionOrder);
                    }

                    // 3) Call local SP CSP_ZANCANER_ORDERS_STOP_ORDER(@ORDER,@SLITTER,@USERID)
                    var localStopSuccess = false;
                    try
                    {
                        await using var conn2 = new SqlConnection(cs);
                        await using var cmd2 = new SqlCommand("CSP_ZANCANER_ORDERS_STOP_ORDER", conn2) { CommandType = CommandType.StoredProcedure };
                        cmd2.Parameters.AddWithValue("@ORDER", productionOrder);
                        cmd2.Parameters.AddWithValue("@SLITTER", slitter);
                        cmd2.Parameters.AddWithValue("@USERID", userId);

                        await conn2.OpenAsync();
                        await cmd2.ExecuteNonQueryAsync();

                        localStopSuccess = true;

                        // local stop success -> log
                        try
                        {
                            await _dbLog.LogAsync(userId, "STOP_ORDER_LOCAL", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), string.Empty);
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogWarning(logEx, "Failed to write STOP_ORDER_LOCAL to DB bitacora for order {Order}", productionOrder);
                        }
                    }
                    catch (Exception exLocal)
                    {
                        // log failure in bitacora with error message
                        try
                        {
                            await _dbLog.LogAsync(userId, "STOP_ORDER_LOCAL_FAILED", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), exLocal.ToString());
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogWarning(logEx, "Failed to write STOP_ORDER_LOCAL_FAILED to DB bitacora for order {Order}", productionOrder);
                        }

                        // proceed to update status accordingly
                    }

                    // 4) Update status depending on local stop result: 930 on success, 920 on failure
                    try
                    {
                        await using var updConn = new SqlConnection(cs);
                        await using var updCmd = new SqlCommand("CSP_ZANCANER_ORDERS_UPDATE_STATUS_BY_ORDER", updConn) { CommandType = CommandType.StoredProcedure };
                        var newStatus = localStopSuccess ? 930 : 920;
                        updCmd.Parameters.AddWithValue("@NEW_STATUS", newStatus);
                        updCmd.Parameters.AddWithValue("@ORDER", productionOrder);
                        await updConn.OpenAsync();
                        await updCmd.ExecuteNonQueryAsync();

                        if (!localStopSuccess)
                        {
                            // log that update corresponds to local stop failure
                            try
                            {
                                await _dbLog.LogAsync(userId, "STOP_ORDER_UPDATE_STATUS", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), $"Updated status to {newStatus} due to local stop failure");
                            }
                            catch { }
                        }
                    }
                    catch (Exception updEx)
                    {
                        _logger.LogError(updEx, "Failed to update status for order {Order}", productionOrder);
                        try
                        {
                            await _dbLog.LogAsync(userId, "STOP_ORDER_UPDATE_STATUS_FAILED", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), updEx.ToString());
                        }
                        catch (Exception logEx)
                        {
                            _logger.LogWarning(logEx, "Failed to write STOP_ORDER_UPDATE_STATUS_FAILED to DB bitacora for order {Order}", productionOrder);
                        }

                        return StatusCode(500, $"Failed to update order status: {updEx.Message}");
                    }

                    // final success log
                    try
                    {
                        await _dbLog.LogAsync(userId, "STOP_ORDER_COMPLETE", $"Production_Order={productionOrder}", Guid.TryParse(messageId, out var g) ? g : Guid.NewGuid(), string.Empty);
                    }
                    catch (Exception logEx)
                    {
                        _logger.LogWarning(logEx, "Failed to write STOP_ORDER_COMPLETE to DB bitacora for order {Order}", productionOrder);
                    }

                    return NoContent();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in StopOrder for order {Order}", productionOrder);
                return StatusCode(500, $"Error stopping order: {ex.Message}");
            }
        }

        [HttpGet("GetProductList/{productionOrder}")]
        public async Task<IActionResult> GetProductList(int productionOrder)
        {
            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs))
            {
                _logger.LogWarning("DefaultConnection not configured.");
                return StatusCode(500, "Connection string not configured.");
            }

            try
            {
                var result = new List<Dictionary<string, object>>();
                await using var conn = new SqlConnection(cs);
                await using var cmd = new SqlCommand("CSP_METRICS_ORDENDESPRODUCCION_GetProductListXOP", conn)
                {
                    CommandType = CommandType.StoredProcedure
                };

                cmd.Parameters.AddWithValue("@PNROOP", productionOrder);

                await conn.OpenAsync();
                await using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var name = reader.GetName(i);
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        row[name] = value;
                    }
                    result.Add(row);
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting product list for order {ProductionOrder}", productionOrder);
                return StatusCode(500, $"Error getting product list: {ex.Message}");
            }
        }
    }
}
