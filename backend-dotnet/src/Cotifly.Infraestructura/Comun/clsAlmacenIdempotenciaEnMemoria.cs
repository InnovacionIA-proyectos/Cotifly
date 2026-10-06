using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Comun;

/// <summary>
/// <see cref="IClsAlmacenIdempotencia"/> en la memoria del proceso (singleton): un semáforo POR CLAVE serializa las repeticiones
/// simultáneas y el resultado que se guarda queda <see cref="IClsAlmacenIdempotencia.Vigencia"/>.
/// </summary>
/// <remarks>
/// Válido mientras la API corre con una sola réplica (<c>infra/stack.dotnet.dokploy.yml</c>, <c>replicas: 1</c>). Un reinicio olvida
/// las claves; la defensa que queda es el <c>If-Match</c> obligatorio (la segunda petición responde <c>CONFLICTO_CONCURRENCIA</c>). Con
/// varias réplicas se pasa a una tabla con un script <c>0008</c> (ADR-018 §12).
/// <para>Cada entrada cuenta cuántas peticiones la están usando (bajo <see cref="_candado"/>): la purga solo quita una entrada sin
/// usuarios, así dos peticiones con la misma clave nunca obtienen semáforos distintos.</para>
/// </remarks>
public sealed class clsAlmacenIdempotenciaEnMemoria(IClsReloj reloj) : IClsAlmacenIdempotencia
{
    private readonly object _candado = new();
    private readonly Dictionary<string, clsEntrada> _entradas = new(StringComparer.Ordinal);

    /// <summary>Claves recordadas (para pruebas y diagnóstico).</summary>
    public int Cantidad
    {
        get
        {
            lock (_candado)
            {
                return _entradas.Count;
            }
        }
    }

    public async Task<clsResultado<T>> EjecutarUnaVezAsync<T>(
        string ambito,
        string clave,
        string huella,
        Func<CancellationToken, Task<clsResultado<T>>> accion,
        Func<clsResultado<T>, bool>? seGuarda,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ambito);
        ArgumentException.ThrowIfNullOrWhiteSpace(huella);
        ArgumentNullException.ThrowIfNull(accion);
        if (string.IsNullOrWhiteSpace(clave)
            || clave.Length is < IClsAlmacenIdempotencia.LargoMinimoClave or > IClsAlmacenIdempotencia.LargoMaximoClave)
        {
            return new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida,
                $"Idempotency-Key debe tener entre {IClsAlmacenIdempotencia.LargoMinimoClave} y {IClsAlmacenIdempotencia.LargoMaximoClave} caracteres.");
        }

        var llave = $"{ambito}\n{clave}";
        var entrada = Tomar(llave);
        try
        {
            await entrada.Semaforo.WaitAsync(ct);
            try
            {
                if (entrada.Guardado is { } guardado && entrada.Expira > reloj.AhoraUtc)
                {
                    return entrada.Huella == huella
                        ? (clsResultado<T>)guardado
                        : new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida,
                            "Esta Idempotency-Key ya se usó con otra petición: genera una clave nueva para cada intención.");
                }

                var resultado = await accion(ct);
                if (seGuarda?.Invoke(resultado) ?? resultado.EsExitoso)
                {
                    entrada.Huella = huella;
                    entrada.Guardado = resultado;
                    entrada.Expira = reloj.AhoraUtc.Add(IClsAlmacenIdempotencia.Vigencia);
                }
                else
                {
                    entrada.Guardado = null;
                }

                return resultado;
            }
            finally
            {
                entrada.Semaforo.Release();
            }
        }
        finally
        {
            Soltar(llave, entrada);
        }
    }

    /// <summary>Entrada de la clave (la crea si no existe) con un usuario más; purga de paso las vencidas y fallidas sin usuarios.</summary>
    private clsEntrada Tomar(string llave)
    {
        lock (_candado)
        {
            var ahora = reloj.AhoraUtc;
            foreach (var (otra, vieja) in _entradas.Where(e => e.Value.Usuarios == 0 && (e.Value.Guardado is null || e.Value.Expira <= ahora)).ToList())
            {
                _entradas.Remove(otra);
                vieja.Semaforo.Dispose();
            }

            if (!_entradas.TryGetValue(llave, out var entrada))
            {
                entrada = new clsEntrada();
                _entradas[llave] = entrada;
            }

            entrada.Usuarios++;
            return entrada;
        }
    }

    /// <summary>Un usuario menos; una clave sin resultado guardado y sin usuarios se olvida enseguida.</summary>
    private void Soltar(string llave, clsEntrada entrada)
    {
        lock (_candado)
        {
            entrada.Usuarios--;
            if (entrada.Usuarios == 0 && entrada.Guardado is null && _entradas.Remove(llave))
            {
                entrada.Semaforo.Dispose();
            }
        }
    }

    private sealed class clsEntrada
    {
        public SemaphoreSlim Semaforo { get; } = new(1, 1);
        public int Usuarios { get; set; }
        public string? Huella { get; set; }
        public object? Guardado { get; set; }
        public DateTimeOffset Expira { get; set; }
    }
}
