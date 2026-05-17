using UnityEngine;
using Mirror;

namespace RPG.Network
{
    /// <summary>
    /// Entry point do servidor dedicado. Inicializa em headless mode e
    /// faz host start automático.
    ///
    /// === COMO USAR ===
    ///   Build do servidor: BuildTarget Server Build, ou rodar com
    ///   -batchmode -nographics -server.
    ///
    /// === MUDANÇAS DESTA VERSÃO (guards anti-conflito) ===
    ///
    ///   PROBLEMA QUE MOTIVOU: usuário tinha UNITY_SERVER definido no Editor
    ///   E rodava um servidor headless build em paralelo. Quando dava Play
    ///   no Editor, ServerEntryPoint detectava UNITY_SERVER, tentava
    ///   levantar servidor na MESMA porta do headless já rodando, e o
    ///   socket bind falhava com SocketException.
    ///
    ///   GUARDS ADICIONADOS:
    ///
    ///   1. Editor não-batchmode NUNCA roda como servidor.
    ///      Mesmo com UNITY_SERVER definido nos Scripting Symbols, se
    ///      você está no Editor com janela gráfica, o ServerEntryPoint
    ///      pula. Para rodar como servidor no Editor, use Play em modo
    ///      batch via linha de comando.
    ///
    ///   2. Pula se NetworkServer.active.
    ///      Cobre o caso de outro bootstrapper já ter levantado o servidor.
    ///
    ///   3. SocketException capturada.
    ///      Em vez de stack trace, loga mensagem clara: "Porta X já em uso
    ///      por outro processo". Não fecha o app — apenas reporta.
    ///
    ///   LOGS MAIS DESCRITIVOS (mantido): versão, modo, porta, timestamp.
    /// </summary>
    public class ServerEntryPoint : MonoBehaviour
    {
        [SerializeField] private NetworkManager networkManager;

        [Tooltip("Se true, ignora o detector de Editor e tenta levantar servidor mesmo " +
                 "no Editor não-batchmode. Útil só pra debug de fluxo de servidor — " +
                 "desligue antes de rodar build headless em paralelo.")]
        [SerializeField] private bool forceStartInEditor = false;

        private void Start()
        {
            if (!ShouldRunAsServer())
            {
                Debug.Log("[ServerEntryPoint] Pulado — não é contexto de servidor.");
                return;
            }

            // Guard contra outro bootstrapper já ter ativado o servidor
            if (NetworkServer.active)
            {
                Debug.Log("[ServerEntryPoint] Servidor já ativo (outro bootstrapper) — pulando StartServer.");
                return;
            }

            if (networkManager == null)
            {
                networkManager = FindObjectOfType<NetworkManager>();
                if (networkManager == null)
                {
                    Debug.LogError("[ServerEntryPoint] NetworkManager não encontrado na cena.");
                    return;
                }
            }

            string version = RPG.Managers.GameManager.GAME_VERSION;
            string mode    = Application.isBatchMode ? "headless" : "graphical";
            ushort port    = ResolveServerPort(networkManager);

            Debug.Log(
                "════════════════════════════════════════════════════════════\n" +
                $"  RPG Online — Server Starting\n" +
                $"  Version:  {version}\n" +
                $"  Mode:     {mode}\n" +
                $"  Port:     {port}\n" +
                $"  Time:     {System.DateTime.UtcNow:o}\n" +
                "════════════════════════════════════════════════════════════");

            try
            {
                networkManager.StartServer();
                Debug.Log($"[ServerEntryPoint] Servidor escutando na porta {port}.");
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                // Erro mais comum: porta já em uso. Loga clara, não derruba o app.
                Debug.LogError(
                    $"[ServerEntryPoint] Falha ao iniciar servidor na porta {port}: {ex.Message}\n" +
                    $"Provável causa: outro processo (servidor headless? outro Editor?) " +
                    $"já está usando essa porta. Encerre o outro processo ou mude a porta " +
                    $"do Transport.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ServerEntryPoint] Erro inesperado ao iniciar servidor: {ex}");
            }
        }

        /// <summary>
        /// True se devemos levantar servidor neste contexto.
        ///
        /// Regras (em ordem):
        ///   1. Editor + forceStartInEditor=true → sim (override manual)
        ///   2. Editor + não-batchmode → NÃO (evita conflito com headless paralelo)
        ///   3. UNITY_SERVER definido (build de servidor) → sim
        ///   4. Application.isBatchMode (linha de comando -batchmode) → sim
        ///   5. Caso contrário → não
        /// </summary>
        private bool ShouldRunAsServer()
        {
#if UNITY_EDITOR
            if (forceStartInEditor) return true;

            // Editor com janela aberta NUNCA é servidor.
            // Editor em batchmode (raro, mas possível) PODE ser servidor.
            return Application.isBatchMode;
#else
    #if UNITY_SERVER
            return true;
    #else
            return Application.isBatchMode;
    #endif
#endif
        }

        private static ushort ResolveServerPort(NetworkManager nm)
        {
            // Tenta resolver via reflection no transport (KCP, Telepathy, etc).
            // Cada transport expõe a porta de forma diferente; falha silenciosa
            // é aceitável aqui — porta padrão é 7777.
            var transport = Transport.active;
            if (transport == null) return 0;

            var portField = transport.GetType().GetField("port");
            if (portField != null && portField.FieldType == typeof(ushort))
                return (ushort)portField.GetValue(transport);

            var portProp = transport.GetType().GetProperty("Port");
            if (portProp != null && portProp.PropertyType == typeof(ushort))
                return (ushort)portProp.GetValue(transport);

            return 0;
        }
    }
}