using Microsoft.AspNetCore.Mvc;
using BackendV2.Models;
using System.Data;
using Microsoft.Data.SqlClient;
using BackendV2.DTOs;
using BackendV2.Services;
using Microsoft.Extensions.Logging;

namespace BackendV2.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class OrdenesController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly IDbLogService _dbLog;
        private readonly ILogger<OrdenesController> _logger;

        public OrdenesController(IConfiguration configuration, IDbLogService dbLog, ILogger<OrdenesController> logger)
        {
            _configuration = configuration;
            _dbLog = dbLog;
            _logger = logger;
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

                cmd.Parameters.AddWithValue("@ProductionOrder", productionOrder);

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

            try
            {
                await using var conn = new SqlConnection(cs);
                await using var cmd = new SqlCommand("CSP_ZANCANER_ORDERS_DELETE_ORDER", conn)
                {
                    CommandType = CommandType.StoredProcedure
                };

                cmd.Parameters.AddWithValue("@ORDER", productionOrder);

                await conn.OpenAsync();
                var rows = await cmd.ExecuteNonQueryAsync();

                if (rows == 0) return NotFound();
                return NoContent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting order");
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
    }
}
