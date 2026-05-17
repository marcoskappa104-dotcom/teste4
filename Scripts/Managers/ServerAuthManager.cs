using System;
using System.Collections.Generic;
using UnityEngine;
using Mirror;
using RPG.Data;
using RPG.Managers;

namespace RPG.Network
{
    /// <summary>
    /// Gerenciador de autenticação e sessões no servidor.
    ///
    /// === RESPONSABILIDADES ===
    ///
    ///   - Gerar nonce de challenge para cada conexão
    ///   - Validar login server-side (delegando ao DatabaseManager)
    ///   - Rate limit por conexão e por IP
    ///   - Manter sessões ativas e TTL
    ///
    /// === ARQUITETURA: POR QUE MonoBehaviour, NÃO NetworkBehaviour ===
    ///
    ///   Versão anterior herdava de NetworkBehaviour, o que obrigava o
    ///   GameObject a ter um NetworkIdentity — erro de validação no Mirror
    ///   em Edit Mode. Mas este componente NÃO tem estado de rede per-instance:
    ///   nenhum SyncVar, nenhum [ClientRpc], nenhum [Command]. É um
    ///   gerenciador puro de estado server-side, chamado pelos handlers
    ///   de NetworkMessage que rodam no servidor.
    ///
    ///   Atributo [Server] do Mirror só funciona em NetworkBehaviour;
    ///   removido daqui — todos os métodos públicos rodam server-side
    ///   por convenção (são chamados pelos handlers que já estão no
    ///   contexto de servidor).
    ///
    /// === REGISTRO DE HOOKS ===
    ///
    ///   Antes (NetworkBehaviour): OnStartServer/OnStopServer eram chamados
    ///   automaticamente pelo Mirror. Agora (MonoBehaviour) precisamos
    ///   registrar via RegisterServerHooks/UnregisterServerHooks — chame
    ///   estes métodos no RPGNetworkManager.OnStartServer/OnStopServer.
    ///
    ///   Exemplo no RPGNetworkManager:
    ///       public override void OnStartServer() {
    ///           base.OnStartServer();
    ///           ServerAuthManager.Instance?.RegisterServerHooks();
    ///       }
    ///       public override void OnStopServer() {
    ///           ServerAuthManager.Instance?.UnregisterServerHooks();
    ///           base.OnStopServer();
    ///       }
    ///
    /// === LIMITAÇÕES CONHECIDAS ===
    ///
    ///   IP "unknown" PODE BYPASSAR RATE LIMIT POR IP:
    ///   GetRemoteIP retorna "unknown" se conn.address vier vazio. Acontece
    ///   em transports como WebSocket-behind-proxy mal configurado (KCP
    ///   normalmente preenche). Múltiplos clients "unknown" compartilham
    ///   o mesmo bucket — ainda limita o grupo, mas pode causar bans
    ///   incorretos. Para produção, configure o transport para preencher
    ///   conn.address (X-Forwarded-For atrás de proxy).
    /// </summary>
    public class ServerAuthManager : MonoBehaviour
    {
        public static ServerAuthManager Instance { get; private set; }

        // ── Configurações ──────────────────────────────────────────────────
        private const int   LOGIN_MAX_PER_CONN      = 5;
        private const int   LOGIN_MAX_PER_IP        = 15;
        private const float IP_BAN_DURATION_SECONDS = 300f;
        private const float MIN_TIME_BETWEEN_LOGINS = 0.5f;
        private const float SESSION_TTL_SECONDS     = 300f;

        // ── Estado ─────────────────────────────────────────────────────────
        private readonly Dictionary<int, ConnectionState> _connectionStates = new();
        private readonly Dictionary<string, IPState>      _ipStates         = new();
        private readonly Dictionary<int, SessionData>     _sessions         = new();

        private bool _hooksRegistered;

        private class ConnectionState
        {
            public string Nonce        = "";
            public int    AttemptsCount;
            public float  LastAttemptTime;
        }

        private class IPState
        {
            public int   AttemptsCount;
            public float FirstAttemptTime;
            public float BanUntilTime;
        }

        private class SessionData
        {
            public string Username;
            public string SelectedCharId;
            public float  CreatedAt;
        }

        // ══════════════════════════════════════════════════════════════════
        // Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[ServerAuthManager] Já existe uma instância — destruindo duplicata.");
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            UnregisterServerHooks();
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Chame em RPGNetworkManager.OnStartServer.
        /// Idempotente — chamar várias vezes não causa problema.
        /// </summary>
        public void RegisterServerHooks()
        {
            if (_hooksRegistered) return;
            NetworkServer.OnDisconnectedEvent += OnConnectionDisconnected;
            _hooksRegistered = true;
            Debug.Log("[ServerAuthManager] Hooks registrados.");
        }

        /// <summary>
        /// Chame em RPGNetworkManager.OnStopServer. Também chamado em OnDestroy.
        /// Idempotente. Limpa todo o estado de sessões/conexões/IPs.
        /// </summary>
        public void UnregisterServerHooks()
        {
            if (!_hooksRegistered) return;
            NetworkServer.OnDisconnectedEvent -= OnConnectionDisconnected;
            _hooksRegistered = false;

            _connectionStates.Clear();
            _ipStates.Clear();
            _sessions.Clear();
            Debug.Log("[ServerAuthManager] Hooks desregistrados, estado limpo.");
        }

        private void OnConnectionDisconnected(NetworkConnectionToClient conn)
        {
            _connectionStates.Remove(conn.connectionId);
            _sessions.Remove(conn.connectionId);
        }

        // ══════════════════════════════════════════════════════════════════
        // API pública (chamada pelos handlers de NetworkMessage do servidor)
        // ══════════════════════════════════════════════════════════════════

        public string GenerateNonceForConnection(int connectionId)
        {
            string nonce = GameManager.GenerateNonce();
            if (!_connectionStates.TryGetValue(connectionId, out var state))
            {
                state = new ConnectionState();
                _connectionStates[connectionId] = state;
            }
            state.Nonce = nonce;
            return nonce;
        }

        public LoginResult TryLogin(NetworkConnectionToClient conn, string username,
                                    string signedHash)
        {
            int connectionId = conn.connectionId;
            string ip = GetRemoteIP(conn);

            if (IsIpBanned(ip, out float banSecondsLeft))
                return LoginResult.Fail($"IP temporariamente bloqueado. Tente novamente em {banSecondsLeft:0}s.");

            if (!_connectionStates.TryGetValue(connectionId, out var state))
                return LoginResult.Fail("Sessão inválida — reconecte.");

            float now = Time.time;
            if (now - state.LastAttemptTime < MIN_TIME_BETWEEN_LOGINS)
                return LoginResult.Fail("Muitas tentativas rápidas — espere um momento.");
            state.LastAttemptTime = now;

            state.AttemptsCount++;
            if (state.AttemptsCount > LOGIN_MAX_PER_CONN)
            {
                conn.Disconnect();
                return LoginResult.Fail("Limite de tentativas atingido.");
            }

            RegisterIpAttempt(ip);

            if (string.IsNullOrEmpty(state.Nonce))
                return LoginResult.Fail("Nonce ausente — reconecte.");

            var attempt = DatabaseManager.Instance.TryLoginWithSignedHash(
                username, signedHash, state.Nonce);

            if (attempt.SuggestedDelayMs > 0)
                System.Threading.Thread.Sleep(attempt.SuggestedDelayMs);

            if (!attempt.Success)
                return LoginResult.Fail("Usuário ou senha inválidos.");

            // Renova nonce a cada login bem-sucedido — defesa adicional
            state.Nonce = GameManager.GenerateNonce();

            _sessions[connectionId] = new SessionData
            {
                Username  = attempt.Account.Username,
                CreatedAt = now
            };

            return LoginResult.Success(attempt.Account);
        }

        public bool IsSessionValid(int connectionId, out string username)
        {
            username = "";
            if (!_sessions.TryGetValue(connectionId, out var session)) return false;
            if (Time.time - session.CreatedAt > SESSION_TTL_SECONDS)
            {
                _sessions.Remove(connectionId);
                return false;
            }
            username = session.Username;
            return true;
        }

        public void SetSelectedCharacter(int connectionId, string characterId)
        {
            if (_sessions.TryGetValue(connectionId, out var session))
                session.SelectedCharId = characterId;
        }

        public string GetSelectedCharacter(int connectionId)
        {
            return _sessions.TryGetValue(connectionId, out var session)
                ? session.SelectedCharId
                : null;
        }

        // ══════════════════════════════════════════════════════════════════
        // Rate limit por IP
        // ══════════════════════════════════════════════════════════════════

        private bool IsIpBanned(string ip, out float secondsLeft)
        {
            secondsLeft = 0f;
            if (string.IsNullOrEmpty(ip)) return false;
            if (!_ipStates.TryGetValue(ip, out var state)) return false;

            if (state.BanUntilTime > Time.time)
            {
                secondsLeft = state.BanUntilTime - Time.time;
                return true;
            }

            if (state.BanUntilTime > 0f && state.BanUntilTime <= Time.time)
            {
                state.BanUntilTime     = 0f;
                state.AttemptsCount    = 0;
                state.FirstAttemptTime = 0f;
            }

            return false;
        }

        private void RegisterIpAttempt(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return;

            if (!_ipStates.TryGetValue(ip, out var state))
            {
                state = new IPState { FirstAttemptTime = Time.time };
                _ipStates[ip] = state;
            }

            state.AttemptsCount++;
            if (state.AttemptsCount >= LOGIN_MAX_PER_IP)
            {
                state.BanUntilTime = Time.time + IP_BAN_DURATION_SECONDS;
                Debug.LogWarning($"[ServerAuthManager] IP {ip} banido por {IP_BAN_DURATION_SECONDS}s " +
                                 $"após {state.AttemptsCount} tentativas.");
            }
        }

        private static string GetRemoteIP(NetworkConnectionToClient conn)
        {
            if (conn == null) return "unknown";
            return string.IsNullOrEmpty(conn.address) ? "unknown" : conn.address;
        }
    }

    public readonly struct LoginResult
    {
        public readonly bool        Ok;
        public readonly string      ErrorMessage;
        public readonly AccountData Account;

        private LoginResult(bool ok, string err, AccountData account)
        {
            Ok           = ok;
            ErrorMessage = err;
            Account      = account;
        }

        public static LoginResult Success(AccountData a) => new(true, null, a);
        public static LoginResult Fail(string e)         => new(false, e, null);
    }
}