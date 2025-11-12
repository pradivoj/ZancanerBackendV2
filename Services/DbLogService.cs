using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackendV2.Services
{
    public class DbLogService : IDbLogService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DbLogService> _logger;

        public DbLogService(IConfiguration configuration, ILogger<DbLogService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task LogAsync(int userId, string action, string @params, Guid correlationId, string errorMsg = "")
        {
            var cs = _configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs))
            {
                _logger.LogWarning("Connection string 'DefaultConnection' is not configured. Skipping DB log.");
                return;
            }

            try
            {
                await using var conn = new SqlConnection(cs);
                await using var cmd = new SqlCommand("CSP_ZANCANER_BITACORA_INSERT", conn)
                {
                    CommandType = CommandType.StoredProcedure
                };

                cmd.Parameters.AddWithValue("@USERID", userId);
                cmd.Parameters.AddWithValue("@ACTION", action ?? string.Empty);
                cmd.Parameters.AddWithValue("@PARAMS", @params ?? string.Empty);
                cmd.Parameters.AddWithValue("@CORRELATIONID", correlationId);
                cmd.Parameters.AddWithValue("@ERRORMSG", errorMsg ?? string.Empty);

                await conn.OpenAsync();
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                // Log locally so we can see why DB logging failed
                _logger.LogError(ex, "Failed to write to DB bitacora.");
            }
        }
    }
}
