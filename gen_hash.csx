#r "nuget: BCrypt.Net-Next, 4.0.3"
using BCrypt.Net;

var hash = BCrypt.Net.BCrypt.HashPassword("admin");
Console.WriteLine(hash);
