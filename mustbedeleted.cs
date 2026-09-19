using System.IO;

public class BAT91
{
    public static void Dec(ref string[] f, int x, string fo, string key)
    {
        unchecked
        {
            int n = 0;
            int c = 255;
            int q = 0;
            int v = 91;
            int z = f[x].Length;

            byte[] b91 = new byte[256];

            while (c > 0)
                b91[c--] = 91;

            while (c < 91)
                b91[key[c]] = (byte)c++;

            using (var output = new FileStream(fo, FileMode.Create))
            {
                for (int i = 0; i != z; i++)
                {
                    c = b91[f[x][i]];

                    if (c == 91)
                        continue;

                    if (v == 91)
                    {
                        v = c;
                    }
                    else
                    {
                        v += c * 91;
                        q |= v << n;

                        if ((v & 8191) > 88)
                            n += 13;
                        else
                            n += 14;

                        v = 91;

                        do
                        {
                            output.WriteByte((byte)q);
                            q >>= 8;
                            n -= 8;
                        }
                        while (n > 7);
                    }
                }

                if (v != 91)
                    output.WriteByte((byte)(q | v << n));
            }
        }
    }
}