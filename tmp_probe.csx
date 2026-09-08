using Syncly.Backend.ProtonDrive;
using Org.BouncyCastle.Bcpg.OpenPgp;
using System.Text;

var passphrase = Encoding.UTF8.GetBytes(ProtonPgp.GeneratePassphrase());
var (armored, keys) = ProtonPgp.GenerateNodeKey(passphrase);
Console.WriteLine('=== NodeKey ===');
Console.WriteLine(armored);
Console.WriteLine('Signing alg: ' + keys.SigningPublic.Algorithm);
Console.WriteLine('Enc alg: ' + keys.EncryptionPublic.Algorithm);
Console.WriteLine('Enc IsEncryptionKey: ' + keys.EncryptionPublic.IsEncryptionKey);
Console.WriteLine('Enc KeyId: ' + keys.EncryptionPublic.KeyId.ToString('X'));
Console.WriteLine('Enc Version: ' + keys.EncryptionPublic.Version);
foreach (PgpSignature sig in keys.EncryptionPublic.GetSignatures())
{
    Console.WriteLine('Sig type=' + sig.SignatureType + ' keyAlg=' + sig.KeyAlgorithm + ' hash=' + sig.HashAlgorithm + ' keyId=' + sig.KeyId.ToString('X'));
}
var (packet, session) = ProtonPgp.CreateContentKey(keys);
Console.WriteLine('Packet len: ' + packet.Length + ' b64: ' + Convert.ToBase64String(packet).Length);
Console.WriteLine('Packet hex: ' + Convert.ToHexString(packet));
