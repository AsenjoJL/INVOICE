using HazelInvoice.Models;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Data;

public static class DbInitializer
{
    public static async Task Initialize(ApplicationDbContext context)
    {
        // Ensure database is created
        await context.Database.MigrateAsync();

        // 1. Seed Customers
        if (!await context.Customers.AnyAsync())
        {
            var customers = new List<Customer>
            {
                new Customer { Name = "Autoliv" },
                new Customer { Name = "NKC" },
                new Customer { Name = "Teradyne" },
                new Customer { Name = "Lear 5" },
                new Customer { Name = "MITSUMI" },
                new Customer { Name = "Global" },
                new Customer { Name = "GMC" },
                new Customer { Name = "JP Morgan" },
                new Customer { Name = "Knowles" },
                new Customer { Name = "Lexmark" },
                new Customer { Name = "Mai" },
                new Customer { Name = "M-land" },
                new Customer { Name = "M-Polo" },
                new Customer { Name = "Montage" },
                new Customer { Name = "MPT" },
                new Customer { Name = "Muramuto" },
                new Customer { Name = "P-mactan" },
                new Customer { Name = "QBE" },
                new Customer { Name = "Radisson" },
                new Customer { Name = "SCI" },
                new Customer { Name = "Taiyo" },
                new Customer { Name = "W-lahug" },
                new Customer { Name = "Cebu Kitchen" },
                new Customer { Name = "Feeder" },
                new Customer { Name = "PHOKIM" }
            };
            await context.Customers.AddRangeAsync(customers);
            await context.SaveChangesAsync();
        }

        // 2. Seed Products
        if (!await context.Products.AnyAsync())
        {
            var productsList = new List<string>
            {
                "Atsuete", "Alugbati", "Amahong", "Ampalaya", "Apog", "American Lemon", "Atis", "Anahaw",
                "Baboy", "Bagoong", "Balat ng Lumpia", "Banana Leaves", "Baguio Beans", "Batong", "Basil Leaves", 
                "Black Pepper", "Bilog", "Black Beans", "Bijon", "Bombay White", "Sibuyas", "Bombay", "Monggo", 
                "Broccoli", "Brussel Sprouts", "Bunzel", "Butuanon", "Buwad", "Bulaklak ng Kalabasa", "Bihon", "Beans", 
                "Cabbage", "Carrots", "Camote Kay", "Cauliflower", "Celery", "Chicken", "Chinese Kangkong", "Chinese Petchay", 
                "Curry Powder", "Chili Powder", "Cornstarch", "Carajay", "Dilaw", "Espada", "Fish", "Fishball", "French Fries", 
                "Gabi", "Gabi (Pak)", "Galay", "Gata", "Ginamos", "Green Peas", "Ground Pork", "Guisado", "Hipon", "Hibe", 
                "Hoddog", "Halabos", "Ham", "Hotdog", "Inasal", "Itlog", "Isda", "Kalamansi", "Kamatis", "Kamote", "Kangkong", 
                "Karne", "Keso", "Kintsay", "Kinchay", "Labanos", "Langka", "Lechon", "Lemon", "Liver", "Luya", "Macaroni", 
                "Manok", "Manga", "Mais", "Mantika", "Mani", "Monggo", "Mustasa", "Native", "Nangka", "Noodles", "Oyster Sauce", 
                "Okra", "Orange", "Onion", "Parsley", "Patola", "Papaya", "Paminta", "Pancit", "Pandan", "Pechay", "Petsay", 
                "Pinya", "Pork", "Pork Chop", "Puso ng Saging", "Radish", "Raisin", "Sangki", "Sapsap", "Sibuyas", "Sili", 
                "Sinigang Mix", "Sitaw", "Saging", "Sotanghon", "Soy Sauce", "Squid Ball", "Salted Peanuts", "Talong", 
                "Tanglad", "Togue", "Tuyo", "Towa", "Talbos ng Kamote", "Upo", "Ube", "Vanilla", "Vinegar", "Watermelon", 
                "White Pepper", "Yellow Fin"
            };

            var products = productsList.Select(name => new Product 
            { 
                Name = name, 
                Unit = "pcs/kg", // Default unit 
                UnitCost = 0,
                IsActive = true,
                ReorderLevel = 10,
                Category = "General"
            }).ToList();

            await context.Products.AddRangeAsync(products);
            await context.SaveChangesAsync();
        }
    }
}
