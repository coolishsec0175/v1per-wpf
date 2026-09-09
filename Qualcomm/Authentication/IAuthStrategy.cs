using System.Threading;
using System.Threading.Tasks;
using v1per_wpf.Qualcomm;

namespace V1Per.Qualcomm.Authentication
{
    public interface IAuthStrategy
    {
        string Name { get; }
        Task<bool> AuthenticateAsync(Comm comm, string programmerPath, CancellationToken ct = default);
    }
}
