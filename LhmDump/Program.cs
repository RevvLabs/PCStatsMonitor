using System;
using LibreHardwareMonitor.Hardware;

class Program
{
    public class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) { computer.Traverse(this); }
        public void VisitHardware(IHardware hardware) { hardware.Update(); foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this); }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    static void Main(string[] args)
    {
        // Write next to the exe so the elevated run's output is easy to find
        string outPath = System.IO.Path.Combine(AppContext.BaseDirectory, "sensors.txt");
        using var writer = new System.IO.StreamWriter(outPath);

        Computer computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
        };
        computer.Open();

        // Two update passes 1s apart — RAPL power and some counters need a delta
        computer.Accept(new UpdateVisitor());
        System.Threading.Thread.Sleep(1000);
        computer.Accept(new UpdateVisitor());

        void DumpHardware(IHardware hardware, string indent)
        {
            writer.WriteLine($"{indent}Hardware: {hardware.Name} ({hardware.HardwareType})");
            foreach (var sensor in hardware.Sensors)
            {
                writer.WriteLine($"{indent}  '{sensor.Name}' | {sensor.SensorType} | Value: {sensor.Value?.ToString() ?? "null"} | Min: {sensor.Min} | Max: {sensor.Max}");
            }
            foreach (var sub in hardware.SubHardware)
                DumpHardware(sub, indent + "  ");
        }

        foreach (var hardware in computer.Hardware)
            DumpHardware(hardware, "");

        computer.Close();
        Console.WriteLine($"written: {outPath}");
    }
}
