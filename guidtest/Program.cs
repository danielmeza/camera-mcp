using System;

var g = Guid.NewGuid();
Console.WriteLine($"Full Guid N format: {g.ToString("N")}");
Console.WriteLine($"Length: {g.ToString("N").Length}");
Console.WriteLine($"First 8 chars: {g.ToString("N")[..8]}");
Console.WriteLine($"Bits: 32 hex chars = 128 bits");
Console.WriteLine($"8 hex chars = 32 bits");
