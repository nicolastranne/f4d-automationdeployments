using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace DeployFront.Pages.ServiceMap
{
    public class EditModel : PageModel
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
            // Only update customer and notes fields
            using var client = new HttpClient();
            var getResponse = await client.GetFromJsonAsync<ServiceMap>($"http://localhost:5000/api/servicemap/{ServiceMap.id}");
            if (getResponse == null)
            {
                ModelState.AddModelError(string.Empty, "Record not found.");
                return Page();
            }
            getResponse.customer = ServiceMap.customer;
            getResponse.notes = ServiceMap.notes;
            var response = await client.PutAsJsonAsync($"http://localhost:5000/api/servicemap/{ServiceMap.id}", getResponse);
            if (response.IsSuccessStatusCode)
                return RedirectToPage("Index");
            ModelState.AddModelError(string.Empty, "Failed to update record.");
            return Page();
        }
    }
}
