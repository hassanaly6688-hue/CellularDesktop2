namespace CellularDesktop.Models;

public class Contact
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DisplayName { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
}
