using UnityEngine;
using UnityEngine.SceneManagement;
using Mirror;
using RPG.Data;
using System.Collections;
using System.Collections.Generic;
using System;

namespace RPG.Network
{
    /// <summary>
    /// Lado cliente da autenticação. Mantém o nonce da sessão e envia
    /// requisições de login/criação/seleção ao servidor.
    ///
    /// Singleton DontDestroyOnLoad — sobrevive a trocas de cena.
    ///
    /// === MUDANÇAS DESTA VERSÃO (defesa contra nonce hijacking) ===
    ///
    ///   1. ACEITA APENAS O PRIMEIRO MsgAuthChallenge DA SESSÃO:
    ///      O protocolo normal só envia UM challenge por conexão (no connect).
    ///      Mas se um servidor malicioso ou um bug enviasse múltiplos
    ///      challenges, a versão anterior aceitava o último silenciosamente,
    ///      sobrescrevendo o nonce que o cliente já podia ter usado.
    ///      Agora marcamos a sessão como "challenge consumed" no primeiro
    ///      e ignoramos subsequentes com log de warning.
    ///
    ///      Reset acontece em OnClientDisconnected, então uma nova conexão
    ///      pode receber um novo challenge normalmente.
    /// </summary>
    public class ClientAuthHandler : MonoBehaviour
    {
        public static ClientAuthHandler Instance { get; private set; }

        public event Action<bool, string>                         OnLoginResult;
        public event Action<bool, string>                         OnCreateAccountResult;
        public event Action<List<CharacterSummary>>               OnCharacterListReceived;
        public event Action<bool, string, List<CharacterSummary>> OnCreateCharacterResult;
        public event Action<bool, string>                         OnSelectCharacterResult;
        public event Action                                       OnServerDisconnected;

        private const float NONCE_WAIT_TIMEOUT = 5f;

        private bool      _waitingForSceneToLoad;
        private string    _sessionNonce  = "";
        private bool      _nonceReceived;
        // Marca que já consumimos o challenge desta sessão. Resetado em disconnect.
        // Defesa contra servidor malicioso enviando múltiplos challenges.
        private bool      _challengeConsumed;
        private Action    _pendingLoginAction;
        private Coroutine _nonceWaitCoroutine;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            NetworkClient.OnConnectedEvent    += OnClientConnected;
            NetworkClient.OnDisconnectedEvent += OnClientDisconnectedEvent;
        }

        private void OnDestroy()
        {
            NetworkClient.OnConnectedEvent    -= OnClientConnected;
            NetworkClient.OnDisconnectedEvent -= OnClientDisconnectedEvent;
            SceneManager.sceneLoaded          -= OnSceneLoaded;

            StopAllPendingWork();
        }

        private void StopAllPendingWork()
        {
            if (_nonceWaitCoroutine != null)
            {
                StopCoroutine(_nonceWaitCoroutine);
                _nonceWaitCoroutine = null;
            }
            _pendingLoginAction    = null;
            _waitingForSceneToLoad = false;
        }

        // ── Conexão ────────────────────────────────────────────────────────

        private void OnClientConnected()
        {
            _nonceReceived     = false;
            _challengeConsumed = false;
            _sessionNonce      = "";

            // ReplaceHandler: substitui se já existir (importante na reconexão)
            NetworkClient.ReplaceHandler<MsgAuthChallenge>          (OnAuthChallenge);
            NetworkClient.ReplaceHandler<MsgLoginResponse>          (OnLoginResponse);
            NetworkClient.ReplaceHandler<MsgCreateAccountResponse>  (OnCreateAccountResponse);
            NetworkClient.ReplaceHandler<MsgCharacterListResponse>  (OnCharacterListResponse);
            NetworkClient.ReplaceHandler<MsgCreateCharacterResponse>(OnCreateCharacterResponse);
            NetworkClient.ReplaceHandler<MsgSelectCharacterResponse>(OnSelectCharacterResponse);
        }

        private void OnClientDisconnectedEvent()
        {
            // Reset IMEDIATO do estado de nonce — antes do StopAllPendingWork.
            _nonceReceived     = false;
            _challengeConsumed = false;
            _sessionNonce      = "";

            StopAllPendingWork();

            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        public void OnDisconnectedFromServer()
        {
            OnServerDisconnected?.Invoke();
        }

        // ── Challenge ──────────────────────────────────────────────────────

        private void OnAuthChallenge(MsgAuthChallenge msg)
        {
            // SEGURANÇA: aceita apenas o PRIMEIRO challenge da sessão.
            // Múltiplos challenges sugerem servidor comprometido ou bug —
            // ignoramos com warning visível para detecção.
            if (_challengeConsumed)
            {
                Debug.LogWarning("[ClientAuth] SECURITY: MsgAuthChallenge adicional recebido — IGNORADO. " +
                                 "Servidor pode estar comprometido ou houve duplicação anômala.");
                return;
            }

            _sessionNonce      = msg.Nonce;
            _nonceReceived     = true;
            _challengeConsumed = true;

            if (_pendingLoginAction != null)
            {
                var action = _pendingLoginAction;
                _pendingLoginAction = null;

                // Cancela coroutine de espera (já temos o nonce)
                if (_nonceWaitCoroutine != null)
                {
                    StopCoroutine(_nonceWaitCoroutine);
                    _nonceWaitCoroutine = null;
                }

                action();
            }
        }

        // ── Envio de requisições ───────────────────────────────────────────

        public void SendLogin(string username, string password)
        {
            if (!NetworkClient.isConnected)
            {
                OnLoginResult?.Invoke(false, "Sem conexão com o servidor.");
                return;
            }

            // Evita sobrescrever uma ação pendente (ex: double-click no botão)
            if (_pendingLoginAction != null)
            {
                Debug.LogWarning("[ClientAuth] SendLogin chamado com ação pendente — ignorando duplicata.");
                return;
            }

            string baseHash = Managers.GameManager.HashPassword(password);
            string user     = username; // captura local

            void DoSend()
            {
                string signedHash = Managers.GameManager.HashPasswordWithNonce(baseHash, _sessionNonce);
                NetworkClient.Send(new MsgLoginRequest
                {
                    Username   = user.Trim(),
                    SignedHash = signedHash
                });
            }

            if (_nonceReceived)
            {
                DoSend();
            }
            else
            {
                _pendingLoginAction = DoSend;

                // Cancela coroutine anterior se houver (sanity, não deveria existir)
                if (_nonceWaitCoroutine != null)
                    StopCoroutine(_nonceWaitCoroutine);

                _nonceWaitCoroutine = StartCoroutine(WaitForNonceThenLogin());
            }
        }

        private IEnumerator WaitForNonceThenLogin()
        {
            float elapsed = 0f;
            while (!_nonceReceived && elapsed < NONCE_WAIT_TIMEOUT)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            _nonceWaitCoroutine = null;

            if (!_nonceReceived)
            {
                _pendingLoginAction = null;
                OnLoginResult?.Invoke(false, "Timeout aguardando o servidor. Tente novamente.");
            }
            // Caso contrário, OnAuthChallenge já executou _pendingLoginAction
        }

        public void SendCreateAccount(string username, string password)
        {
            if (!NetworkClient.isConnected)
            {
                OnCreateAccountResult?.Invoke(false, "Sem conexão com o servidor.");
                return;
            }

            NetworkClient.Send(new MsgCreateAccountRequest
            {
                Username     = username.Trim(),
                PasswordHash = Managers.GameManager.HashPassword(password)
            });
        }

        public void SendRequestCharacterList()
        {
            if (NetworkClient.isConnected)
                NetworkClient.Send(new MsgRequestCharacterList());
        }

        public void SendCreateCharacter(string name, int raceIndex)
        {
            if (NetworkClient.isConnected)
                NetworkClient.Send(new MsgCreateCharacterRequest
                { Name = name.Trim(), RaceIndex = raceIndex });
        }

        public void SendSelectCharacter(string characterId)
        {
            if (NetworkClient.isConnected)
                NetworkClient.Send(new MsgSelectCharacter { CharacterId = characterId });
        }

        // ── Respostas do servidor ──────────────────────────────────────────

        private void OnLoginResponse(MsgLoginResponse msg)
        {
            if (msg.Success)
                Managers.GameManager.Instance?.SetLoggedUsername(msg.Username);
            OnLoginResult?.Invoke(msg.Success, msg.Error);
        }

        private void OnCreateAccountResponse(MsgCreateAccountResponse msg)
            => OnCreateAccountResult?.Invoke(msg.Success, msg.Error);

        private void OnCharacterListResponse(MsgCharacterListResponse msg)
            => OnCharacterListReceived?.Invoke(msg.Characters);

        private void OnCreateCharacterResponse(MsgCreateCharacterResponse msg)
            => OnCreateCharacterResult?.Invoke(msg.Success, msg.Error, msg.UpdatedList);

        private void OnSelectCharacterResponse(MsgSelectCharacterResponse msg)
        {
            OnSelectCharacterResult?.Invoke(msg.Success, msg.Error);

            if (!msg.Success) return;

            _waitingForSceneToLoad = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.LoadScene(Managers.GameManager.SCENE_GAMEPLAY);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!_waitingForSceneToLoad) return;
            if (scene.name != Managers.GameManager.SCENE_GAMEPLAY) return;

            SceneManager.sceneLoaded -= OnSceneLoaded;
            _waitingForSceneToLoad    = false;

            StartCoroutine(SendReadyAfterFrame());
        }

        private IEnumerator SendReadyAfterFrame()
        {
            // 2 frames para o NavMesh e os scripts iniciarem
            yield return null;
            yield return null;

            if (NetworkClient.isConnected)
                NetworkClient.Send(new MsgClientSceneReady());
        }
    }
}
