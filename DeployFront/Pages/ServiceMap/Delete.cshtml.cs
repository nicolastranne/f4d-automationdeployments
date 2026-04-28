using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace DeployFront.Pages.ServiceMap
{
    public class DeleteModel : PageModel
    {
        [BindProperty]
        public ServiceMap ServiceMap { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            using var client = new HttpClient();
            var data = await client.GetFromJsonAsync<ServiceMap>($"http://localhost:5000/api/servicemap/{id}");
            if (data == null)
                return RedirectToPage("Index");
            ServiceMap = data;
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            using var client = new HttpClient();
            var response = await client.DeleteAsync($"http://localhost:5000/api/servicemap/{ServiceMap.id}");
            if (response.IsSuccessStatusCode)
                return RedirectToPage("Index");
            ModelState.AddModelError(string.Empty, "Failed to delete record.");
            return Page();
        }
    }
}
