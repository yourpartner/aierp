using BCrypt.Net;

var hash = BCrypt.Net.BCrypt.HashPassword("itbank2026");
Console.WriteLine(hash);
