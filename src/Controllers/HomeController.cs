using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace OpcPlc.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    internal class HomeController : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            if (System.IO.File.Exists(Program.PnJson))
            {
                var fileContent = await System.IO.File.ReadAllTextAsync(Program.PnJson);
                return Ok(fileContent);
            }
            else
            {
                return NotFound();
            }
        }
    }
}
