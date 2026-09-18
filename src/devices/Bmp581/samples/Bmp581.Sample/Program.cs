using System;
using System.Device.I2c;
using System.Net;
using Iot.Device.Bmp581;
using UnitsNet;

Console.WriteLine("=== BMP581 I2C Diagnostic Tool ===");
Console.WriteLine("Starting scan...\n");

const int busId = 1;

Console.WriteLine("BMP581 Chip Test Y'all");

I2cConnectionSettings settings = new(busId, Bmp581.DefaultI2cAddress);
using I2cDevice device = I2cDevice.Create(settings);

try
{
    Bmp581 sensor = new Bmp581(device);
    Console.WriteLine("Bmp581 detected!");
    
    // Check Oversampling rates
    var tempOversampling = sensor.GetTempOsr();
    var pressOversampling = sensor.GetPressOsr();
    Console.WriteLine($"Temp Oversampling setting is {tempOversampling}, press Oversampling setting is {pressOversampling}");

    // Set Oversampling rate
    var oversamplingSetting = Bmp581.OversamplingRate.SixteenX;
    Console.WriteLine($"Updating temp AND press oversampling setting to {oversamplingSetting}");
    sensor.SetTempOsr(oversamplingSetting);
    sensor.SetPressOsr(oversamplingSetting);

    // Check Oversampling rates again
    tempOversampling = sensor.GetTempOsr();
    pressOversampling = sensor.GetPressOsr();
    Console.WriteLine($"Temp Oversampling setting is {tempOversampling}, press Oversampling setting is {pressOversampling}");

    // Check Current mode
    var currentMode = sensor.GetPowerMode();
    Console.WriteLine($"Power mode is {currentMode}");

    // Update power mode to force a measurement
    sensor.SetPowerMode(Bmp581.PowerMode.Continuous);

    // Check power mode again
    currentMode = sensor.GetPowerMode();
    Console.WriteLine($"Power mode is {currentMode.ToString()}");

    // Check Pressure mode
    bool presureMode = sensor.PressureEnabled;
    Console.WriteLine($"Pressure enabled is: {presureMode}, Enabling now");

    // Set Pressure mode
    sensor.PressureEnabled = true;

    // Check Pressure mode
    presureMode = sensor.PressureEnabled;
    Console.WriteLine($"Pressure enabled is: {presureMode}");

    // Wait for measurement
    Thread.Sleep(50);

    // Read Temperature
    Temperature temp = sensor.ReadTemperature();
    Console.WriteLine($"Current Temp is: {temp.DegreesCelsius}C, That's {temp.DegreesFahrenheit}F");

    // Read Pressure
    Pressure press = sensor.ReadPressure();
    Console.WriteLine($"Current Pressure is: {press.InchesOfMercury} In Hg. Elevation is: {press.FeetOfElevation} ft");

    // Calculate Altitude
    Pressure seaLevelPressure = Pressure.FromInchesOfMercury(29.6);
    Length altitude = sensor.CalculateAltitude(seaLevelPressure);
    Console.WriteLine($"Current altitude is {altitude.Feet} feet, assuming Sea Level pressure of {seaLevelPressure}");
}

catch
{
    Console.Write("FAILED to detect BMP581");
}

