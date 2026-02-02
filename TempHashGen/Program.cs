using BCrypt.Net;

var hash = BCrypt.Net.BCrypt.HashPassword("admin");
Console.WriteLine(hash);
