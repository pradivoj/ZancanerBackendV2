using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;

namespace BackendV2.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MaquinasController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<MaquinasController> _logger;

        public MaquinasController(IConfiguration configuration, ILogger<MaquinasController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        // GET api/maquinas/Getmaquinascortadoras
        [HttpGet("Getmaquinascortadoras")]
        public async Task<IActionResult> Getmaquinascortadoras()
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
                await using var cmd = new SqlCommand("CSP_ZANCANER_LISTAR_MAQUINAS_CORTADORAS", conn)
                {
                    CommandType = CommandType.StoredProcedure
                };

                await conn.OpenAsync();
                await using var reader = await cmd.ExecuteReaderAsync();

                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var name = reader.GetName(i);
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        row[name] = value ?? DBNull.Value;
                    }

                    result.Add(row);
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing maquinas cortadoras");
                return StatusCode(500, $"Error listing maquinas cortadoras: {ex.Message}");
            }
        }
    }
}
