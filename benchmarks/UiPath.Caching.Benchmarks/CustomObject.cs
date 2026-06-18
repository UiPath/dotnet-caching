namespace UiPath.Caching.Benchmarks;

[Serializable]
public class CustomObject
{
    // Adjust the properties to control the size of the serialized object
    public long Property1 { get; set; }
    public string? Property2 { get; set; }
    public double[]? Property3 { get; set; }
    public DateTime UtcDateTime { get; set; }
    public List<Guid>? GuidList { get; set; }

    public static CustomObject RandomLarge()
    {
        var customObject = new CustomObject
        {
            Property1 = GenerateRandomLong(),
            Property2 = GenerateRandomString(1_000),
            Property3 = GenerateRandomDoubleArray(10_000),
            UtcDateTime = DateTime.UtcNow,
            GuidList = GenerateRandomGuidList(5_000)
        };

        return customObject;
    }

    public static CustomObject RandomMedium()
    {
        var customObject = new CustomObject
        {
            Property1 = GenerateRandomLong(),
            Property2 = GenerateRandomString(100),
            Property3 = GenerateRandomDoubleArray(10),
            UtcDateTime = DateTime.UtcNow,
            GuidList = GenerateRandomGuidList(10)
        };

        return customObject;
    }

    public static CustomObject RandomSmall()
    {
        var customObject = new CustomObject
        {
            Property1 = GenerateRandomLong(),
            Property2 = GenerateRandomString(10),
            Property3 = GenerateRandomDoubleArray(2),
            UtcDateTime = DateTime.UtcNow,
        };

        return customObject;

    }

    private static long GenerateRandomLong()
    {
        return BitConverter.ToInt64(GenerateRandomBytes(sizeof(long)), 0);
    }

    private static string GenerateRandomString(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Repeat(chars, length)
          .Select(s => s[Random.Shared.Next(s.Length)]).ToArray());
    }

    private static byte[] GenerateRandomBytes(int length)
    {
        byte[] buffer = new byte[length];
        System.Random.Shared.NextBytes(buffer);
        return buffer;
    }

    private static double[] GenerateRandomDoubleArray(int size)
    {
        double[] array = new double[size];
        Random random = new Random();
        for (int i = 0; i < size; i++)
        {
            array[i] = random.NextDouble();
        }
        return array;
    }

    private static List<Guid> GenerateRandomGuidList(int count)
    {
        List<Guid> guidList = new List<Guid>();
        for (int i = 0; i < count; i++)
        {
            guidList.Add(Guid.NewGuid());
        }
        return guidList;
    }
}
