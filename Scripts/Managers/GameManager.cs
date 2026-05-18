using UnityEngine;
using UnityEngine.SceneManagement;
using System;

namespace RPG.Managers
{
    /// <summary>
    /// Gerenciador global de sessão e autenticação.
    ///
    /// ───────────────────────────────────────────────────────────────────────
    /// PIPELINE DE AUTENTICAÇÃO
    /// ───────────────────────────────────────────────────────────────────────
    ///
    /// Criar conta:
    ///   Cliente:  hash = SHA256(senha)
    ///   Cliente:  envia { Username, PasswordHash = hash }
    ///   Servidor: armazena hash diretamente no banco
    ///
    /// Login:
    ///   Servidor: gera nonce aleatório e envia ao cliente ao conectar
    ///   Cliente:  hash      = SHA256(senha)
    ///   Cliente:  signed    = SHA256(hash + nonce)
    ///   Cliente:  envia { Username, SignedHash = signed }
    ///   Servidor: expected  = SHA256(STORED_HASH + nonce)
    ///   Servidor: aceita se signed == expected
    ///
    /// O nonce previne replay attacks: capturar SignedHash não permite reusar
    /// em outra sessão (nonce diferente => hash diferente).
    ///
    /// ───────────────────────────────────────────────────────────────────────
    /// LIMITAÇÕES DE SEGURANÇA
    /// ───────────────────────────────────────────────────────────────────────
    ///
    /// - SEM TLS: tráfego é vulnerável a MITM ativo (capturar e modificar).
    /// - SEM SALT no banco: se o banco vazar, hashes são SHA256(senha), que
    ///   é quebrável com rainbow tables. Para produção real considere:
    ///     1) bcrypt/Argon2 no servidor (inclui salt automaticamente)
    ///     2) KCP+TLS ou WebSocket+WSS para o transporte
    ///
    /// Para um projeto pessoal ou alfa fechado isso é aceitável. Para release
    /// público, troque para bcrypt antes de aceitar contas reais.
    ///
    /// === MUDANÇAS DESTA VERSÃO ===
    ///
    ///   1. COMENTÁRIO DO ComputeSHA256 ALINHADO COM A IMPLEMENTAÇÃO:
    ///      Versão anterior do comentário mencionava SHA256.HashData (.NET 6+,
    ///      não disponível em Unity 2022.3.62f3). A implementação usa
    ///      SHA256.Create() + ComputeHash, que funciona em todas as versões
    ///      do Mono/.NET Standard 2.1 suportadas pela Unity. Docs corrigidas.
    ///
    ///   2. CONSTANT-TIME COMPARE NO LOGIN (mantido):
    ///      ValidateLoginWithNonce usa comparação byte-a-byte que sempre
    ///      percorre o comprimento total, mitigando timing leaks.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        public string LoggedUsername { get; private set; } = "";

        public const string SCENE_LOGIN     = "LoginScene";
        public const string SCENE_CHARACTER = "CharacterScene";
        public const string SCENE_GAMEPLAY  = "GameplayScene";
        public const string GAME_VERSION    = "0.1.0-alpha";

        // 16 bytes (128 bits) é mais que suficiente para impedir colisão prática
        private const int NONCE_BYTES = 16;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Debug.Log($"[GameManager] Iniciado — versão {GAME_VERSION}");
        }

        public void SetLoggedUsername(string username)
        {
            LoggedUsername = username ?? "";
        }

        public void GoToCharacterSelect() => SceneManager.LoadScene(SCENE_CHARACTER);
        public void GoToGameplay()        => SceneManager.LoadScene(SCENE_GAMEPLAY);

        public void Logout()
        {
            LoggedUsername = "";
            SceneManager.LoadScene(SCENE_LOGIN);
        }

        // ══════════════════════════════════════════════════════════════════
        // Hashing
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// SHA256 da senha. Usado na criação de conta e como base do login.
        /// O cliente nunca deve enviar a senha em texto plano.
        /// </summary>
        public static string HashPassword(string password)
        {
            if (string.IsNullOrWhiteSpace(password)) return "";
            return ComputeSHA256(password);
        }

        /// <summary>
        /// Assina o hash base com o nonce da sessão para login.
        /// Usado pelo cliente no segundo passo.
        /// </summary>
        public static string HashPasswordWithNonce(string passwordHash, string nonce)
        {
            if (string.IsNullOrEmpty(passwordHash) || string.IsNullOrEmpty(nonce))
                return passwordHash;
            return ComputeSHA256(passwordHash + nonce);
        }

        /// <summary>
        /// Gera nonce aleatório de NONCE_BYTES bytes. Chamado pelo servidor por sessão.
        /// Usa RandomNumberGenerator que é criptograficamente seguro.
        /// </summary>
        public static string GenerateNonce()
        {
            var bytes = new byte[NONCE_BYTES];
            using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

#if UNITY_SERVER || UNITY_EDITOR

        public static string ServerHashForStorage(string clientPasswordHash)
        {
            if (string.IsNullOrEmpty(clientPasswordHash))
            {
                Debug.LogError("[GameManager] ServerHashForStorage: hash vazio.");
                return "";
            }
            return clientPasswordHash;
        }

        /// <summary>
        /// Servidor — valida login com comparação de TEMPO CONSTANTE.
        /// Mitigação contra timing attacks: comparar strings com == "vaza"
        /// o tempo até o primeiro byte diferente. Comparação byte-a-byte
        /// constante evita esse vazamento.
        /// </summary>
        public static bool ValidateLoginWithNonce(
            string storedPasswordHash,
            string clientSignedHash,
            string sessionNonce)
        {
            if (string.IsNullOrEmpty(storedPasswordHash)
                || string.IsNullOrEmpty(clientSignedHash)
                || string.IsNullOrEmpty(sessionNonce))
                return false;

            string expected = ComputeSHA256(storedPasswordHash + sessionNonce);
            return FixedTimeStringEquals(expected, clientSignedHash);
        }

        /// <summary>
        /// Comparação de strings em tempo constante. Compara TODOS os bytes
        /// mesmo após encontrar diferença, evitando timing leak.
        /// Case-INsensitive (hashes hex são gerados em lowercase).
        /// </summary>
        private static bool FixedTimeStringEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;

            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                char ca = char.ToLowerInvariant(a[i]);
                char cb = char.ToLowerInvariant(b[i]);
                diff |= (ca ^ cb);
            }
            return diff == 0;
        }

#endif

        /// <summary>
        /// Calcula SHA-256 e retorna como string hexadecimal lowercase.
        ///
        /// Implementação compatível com Unity 2022.3.62f3 (Mono / .NET Standard 2.1):
        /// usa SHA256.Create() + ComputeHash. Não usa SHA256.HashData (.NET 6+,
        /// indisponível nesta versão da Unity).
        /// </summary>
        public static string ComputeSHA256(string input)
        {
            if (string.IsNullOrEmpty(input))
                return "";

            byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(input);

            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(inputBytes);

            return BytesToHexLower(hash);
        }

        /// <summary>
        /// Converte bytes em hex lowercase. Mais rápido que BitConverter
        /// + Replace + ToLower (que faz 3 alocações desnecessárias).
        /// </summary>
        private static string BytesToHexLower(byte[] bytes)
        {
            const string hexChars = "0123456789abcdef";
            var result = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                result[i * 2]     = hexChars[b >> 4];
                result[i * 2 + 1] = hexChars[b & 0x0F];
            }
            return new string(result);
        }
    }
}
