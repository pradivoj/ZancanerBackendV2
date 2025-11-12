using System;
using System.Threading.Tasks;

namespace BackendV2.Services
{
    public interface IDbLogService
    {
        Task LogAsync(int userId, string action, string @params, Guid correlationId, string errorMsg = "");
    }
}
