using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace HazelInvoice.ViewModels;

public class SummaryAllViewModel
{
    public DateTime StartDate { get; set; } = DateTime.Today;
    public DateTime EndDate { get; set; } = DateTime.Today;
    public string StatusFilter { get; set; } = "All"; // All, Paid, Unpaid
    public int? OutletId { get; set; } // Optional Customer/Outlet ID

    // KPIs
    public decimal TotalSales { get; set; } 
    public decimal TotalPaid { get; set; }
    public decimal TotalUnpaid { get; set; }
    public int TotalCount { get; set; }
    public decimal TotalItemsSold { get; set; }

    // Analytics lists
    public List<TopItemDto> TopItems { get; set; } = new();
    public List<OutletSummaryDto> OutletSummaries { get; set; } = new();
    public List<DailyTrendDto> DailyTrends { get; set; } = new();

    // Select Lists
    public SelectList Outlets { get; set; } = new SelectList(Array.Empty<SelectListItem>());
}


