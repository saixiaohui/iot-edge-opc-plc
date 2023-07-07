using Microsoft.AspNetCore.Mvc;

namespace OpcPlc.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class HealthController : ControllerBase
    {
        [HttpGet]
        public ActionResult Health()
        {
            return Ok();
        }
    }
}
