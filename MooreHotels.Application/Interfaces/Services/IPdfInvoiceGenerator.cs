using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IPdfInvoiceGenerator
{
    byte[] GenerateInvoicePdf(BookingDto booking, IEnumerable<BookingAddOnDto>? addOns = null);
}
