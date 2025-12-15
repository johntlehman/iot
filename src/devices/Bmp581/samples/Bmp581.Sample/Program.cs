using System;
using System.Device.I2c;
using System.Net;
using Iot.Device.Bmp581;

Console.WriteLine("=== BMP581 I2C Diagnostic Tool ===");
Console.WriteLine("Starting scan...\n");

const int busId = 1;

// Try both addresses
byte[] addressesToTry = { Bmp581.DefaultI2cAddress, Bmp581.SecondaryI2cAddress };

I2cDevice? workingDevice = null;
byte workingAddress = 0;

foreach (var address in addressesToTry)
{
    Console.WriteLine($"Trying I2C address 0x{address:X2}...");

    try
    {
        I2cConnectionSettings i2cSettings = new(busId, address);
        I2cDevice testDevice = I2cDevice.Create(i2cSettings);

        // Try to read chip ID register directly
        testDevice.WriteByte(0x01); // CHIP_ID register
        byte chipId = testDevice.ReadByte();

        Console.WriteLine($"  Read value: 0x{chipId:X2} (Expected: 0x50)");

        Console.WriteLine("[1] Now for my little test");
        Bmp581 testBmp581 = new Bmp581(testDevice);

        Console.WriteLine($"[2] Results of verifying chip id: {testBmp581.VerifyChipID()}");
        Console.WriteLine("[3] After verification line");

        if (chipId == 0x50)
        {
            Console.WriteLine($"[4] ✓ SUCCESS! Found BMP581 at address 0x{address:X2}");
            workingDevice = testDevice;
            workingAddress = address;
            break;
        }
        else if (chipId == 0x80 || chipId == 0xFF || chipId == 0x00)
        {
            Console.WriteLine($"  ✗ No device responding (got 0x{chipId:X2})\n");
            testDevice.Dispose();
        }
        else
        {
            Console.WriteLine($"  ✗ Wrong device - found chip ID 0x{chipId:X2}\n");
            testDevice.Dispose();
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ Error: {ex.Message}");
        Console.WriteLine($"  Stack: {ex.StackTrace}\n");
    }
}

if (workingDevice != null)
{
    Console.WriteLine($"Using BMP581 at address 0x{workingAddress:X2}");
    using var bmp581 = new Bmp581(workingDevice);
    Console.WriteLine("Driver initialized successfully!");

    // Test reading chip ID through driver
    if (bmp581.VerifyChipID())
    {
        Console.WriteLine("✓ Driver verification successful!\n");
    }
    else
    {
        Console.WriteLine("✗ Driver verification failed!\n");
    }
}
else
{
    Console.WriteLine("✗ FAILED: Could not find BMP581 on I2C bus");
    Console.WriteLine("\nTroubleshooting tips:");
    Console.WriteLine("1. Check wiring (VCC, GND, SDA, SCL)");
    Console.WriteLine("2. Verify I2C is enabled: ls /dev/i2c*");
    Console.WriteLine("3. Check permissions: groups (should include 'i2c')");
    Console.WriteLine("4. Try the other I2C bus (change busId to 0)");
}

Console.WriteLine("\nDiagnostic complete.");

