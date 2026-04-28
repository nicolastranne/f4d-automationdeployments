using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace DeployFront.Pages.ServiceMap
{
    public class CreateModel : PageModel
    {
        [BindProperty]
        public ServiceMap ServiceMap { get; set; } = new();

        public void OnGet() { }

        public async Task<IActionResult> OnPostAsync()
        {
            using var client = new HttpClient();
            var response = await client.PostAsJsonAsync("http://localhost:5000/api/servicemap", ServiceMap);
            if (response.IsSuccessStatusCode)
                return RedirectToPage("Index");
            ModelState.AddModelError(string.Empty, "Failed to create record.");
            return Page();
        }
    }
}
