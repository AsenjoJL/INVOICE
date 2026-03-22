namespace HazelInvoice.Configuration;

/// <summary>
/// Starter seed data used only when a database is empty.
/// Keeping this in config makes fresh installs easier to maintain without affecting live populated databases.
/// </summary>
public sealed class BootstrapSeedOptions
{
    public List<string> CustomerNames { get; set; } =
    [
        "Autoliv",
        "NKC",
        "Teradyne",
        "Lear 5",
        "MITSUMI",
        "Global",
        "GMC",
        "JP Morgan",
        "Knowles",
        "Lexmark",
        "Mai",
        "M-land",
        "M-Polo",
        "Montage",
        "MPT",
        "Muramuto",
        "P-mactan",
        "QBE",
        "Radisson",
        "SCI",
        "Taiyo",
        "W-lahug",
        "Cebu Kitchen",
        "Feeder",
        "PHOKIM"
    ];

    public List<string> ProductNames { get; set; } =
    [
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
    ];
}
