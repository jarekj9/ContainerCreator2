using System.Security.Cryptography;

public static class RandomPasswordGenerator
{
    public static string CreateRandomPassword(int length = 15, bool useSpecialChars = true)
    {
        const string validCharsAll = "ABCDEFGHJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*?_-";
        const string validCharsWithoutSpecial = "ABCDEFGHJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var validChars = useSpecialChars ? validCharsAll : validCharsWithoutSpecial;

        char[] chars = new char[length];

        using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
        {
            byte[] data = new byte[length];

            // Fill the array with cryptographically strong random bytes
            rng.GetBytes(data);

            for (int i = 0; i < length; i++)
            {
                int index = data[i] % validChars.Length;
                chars[i] = validChars[index];
            }
        }

        return new string(chars);
    }
}