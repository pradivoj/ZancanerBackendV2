using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using System.Net.Http;

namespace BackendV2.Controllers
{
    /// <summary>
    /// Health endpoints for the API.
    /// - GET /api/health/live  : liveness probe (is the process running?)
    /// - GET /api/health/ready : readiness probe (can the app access required dependencies?)
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class HealthController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<HealthController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public HealthController(IConfiguration configuration, ILogger<HealthController> logger, IHttpClientFactory httpClientFactory)
        {
            _configuration = configuration;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        /// Liveness probe. Returns 200 OK if the application process is running.
        /// </summary>
        /// <returns>200 OK with a short alive message.</returns>
        [HttpGet("live")]
        public IActionResult Live()
        {
            return Ok(new { status = "Alive", timestamp = DateTime.UtcNow });
        }

        /// <summary>
        /// Readiness probe. Checks connectivity to the configured database and an optional external endpoint.
        /// Returns 200 OK when ready, or 503 Service Unavailable when not ready.
        /// </summary>
        /// <returns>200 OK with details when ready, 503 otherwise.</returns>
        [HttpGet("ready")]
        public async Task<IActionResult> Ready()
        {
            var healthy = true;
            var cs = _configuration.GetConnectionString("DefaultConnection");

            var dbCheck = new { ok = true, error = string.Empty };
            if (string.IsNullOrWhiteSpace(cs))
            {
                dbCheck = new { ok = false, error = "DefaultConnection not configured" };
                healthy = false;
            }
            else
            {
                try
                {
                    await using var conn = new SqlConnection(cs);
                    var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await conn.OpenAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Health readiness: DB connection failed");
                    dbCheck = new { ok = false, error = ex.Message };
                    healthy = false;
                }
            }

            object externalCheck = new { ok = true };
            var external = _configuration.GetValue<string>("RecurringService:ExternalEndpoint");
            if (!string.IsNullOrWhiteSpace(external))
            {
                try
                {
                    var client = _httpClientFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(3);

                    // Use GET; some endpoints may not allow HEAD
                    using var resp = await client.GetAsync(external);
                    externalCheck = new { ok = resp.IsSuccessStatusCode, status = (int)resp.StatusCode };
                    if (!resp.IsSuccessStatusCode) healthy = false;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Health readiness: external endpoint failed");
                    externalCheck = new { ok = false, error = ex.Message };
                    healthy = false;
                }
            }

            var result = new
            {
                status = healthy ? "Ready" : "NotReady",
                timestamp = DateTime.UtcNow,
                checks = new
                {
                    db = dbCheck,
                    external = externalCheck
                }
            };

            return healthy ? Ok(result) : StatusCode(503, result);
        }

        /// <summary>
        /// General health endpoint, equivalent to <c>/api/health/ready</c>.
        /// </summary>
        [HttpGet]
        public Task<IActionResult> Get() => Ready();
    }
}
