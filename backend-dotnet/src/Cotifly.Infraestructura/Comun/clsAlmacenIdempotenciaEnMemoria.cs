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
/// varias réplicas se pasa a una tabla con el siguiente script libre (hoy el <c>0011</c>: el <c>0009</c> y el <c>0010</c> son de HU-021; ADR-018 §12, ADR-019).
/// <para>Cada entrada cuenta cuántas peticiones la están usando (bajo <see cref="_candado"/>): la purga solo quita una entrada sin
/// usuarios, así dos peticiones con la misma clave nunca obtienen semáforos distintos.</para>
/// <para>Memoria acotada (arrastre de #99 en #102): la purga de vencidas y fallidas corre cada <see cref="IntervaloPurga"/> (no en cada
/// petición) o al llegar al tope; con <see cref="MaximoEntradasPorDefecto"/> claves recordadas, una clave nueva desaloja un lote (el 10 %
/// del tope) de las guardadas más próximas a vencer que nadie esté usando. Una clave desalojada antes de su vigencia deja como defensa el
/// <c>If-Match</c> (la repetición responde <c>CONFLICTO_CONCURRENCIA</c>), igual que tras un reinicio.</para>
/// </remarks>
public sealed class clsAlmacenIdempotenciaEnMemoria : IClsAlmacenIdempotencia
{
    /// <summary>Tope de claves recordadas.</summary>
    public const int MaximoEntradasPorDefecto = 20_000;

    /// <summary>Cada cuánto se purgan las claves vencidas y fallidas.</summary>
    public static readonly TimeSpan IntervaloPurga = TimeSpan.FromMinutes(5);

    private readonly object _candado = new();
    private readonly Dictionary<string, clsEntrada> _entradas = new(StringComparer.Ordinal);
    private readonly IClsReloj _reloj;
    private readonly int _maximo;
    private DateTimeOffset _proximaPurga = DateTimeOffset.MinValue;

    public clsAlmacenIdempotenciaEnMemoria(IClsReloj reloj)
        : this(reloj, MaximoEntradasPorDefecto)
    {
    }

    /// <param name="reloj">Reloj de la aplicación (vigencia y purga).</param>
    /// <param name="maximoEntradas">Tope de claves recordadas (las pruebas lo bajan).</param>
    public clsAlmacenIdempotenciaEnMemoria(IClsReloj reloj, int maximoEntradas)
    {
        ArgumentNullException.ThrowIfNull(reloj);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximoEntradas, 1);
        _reloj = reloj;
        _maximo = maximoEntradas;
    }

    /// <summary>Purgas completas hechas (para pruebas y diagnóstico).</summary>
    public int Purgas { get; private set; }

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
                if (entrada.Guardado is { } guardado && entrada.Expira > _reloj.AhoraUtc)
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
                    entrada.Expira = _reloj.AhoraUtc.Add(IClsAlmacenIdempotencia.Vigencia);
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

    /// <summary>
    /// Entrada de la clave (la crea si no existe) con un usuario más. Purga las vencidas y fallidas sin usuarios cada
    /// <see cref="IntervaloPurga"/> o al llegar al tope; si aun así está lleno, desaloja las guardadas sin usuarios más próximas a vencer.
    /// </summary>
    private clsEntrada Tomar(string llave)
    {
        lock (_candado)
        {
            if (!_entradas.TryGetValue(llave, out var entrada))
            {
                var ahora = _reloj.AhoraUtc;
                if (ahora >= _proximaPurga || _entradas.Count >= _maximo)
                {
                    Purgar(ahora);
                }

                if (_entradas.Count >= _maximo)
                {
                    // Por lotes (10 % del tope): ordenar las 20.000 claves en cada alta con el almacén lleno sería caro.
                    Desalojar(Math.Max(_entradas.Count - _maximo + 1, _maximo / 10));
                }

                entrada = new clsEntrada();
                _entradas[llave] = entrada;
            }

            entrada.Usuarios++;
            return entrada;
        }
    }

    /// <summary>Quita las vencidas y las fallidas que nadie usa (bajo <see cref="_candado"/>).</summary>
    private void Purgar(DateTimeOffset ahora)
    {
        foreach (var (otra, vieja) in _entradas.Where(e => e.Value.Usuarios == 0 && (e.Value.Guardado is null || e.Value.Expira <= ahora)).ToList())
        {
            _entradas.Remove(otra);
            vieja.Semaforo.Dispose();
        }

        _proximaPurga = ahora.Add(IntervaloPurga);
        Purgas++;
    }

    /// <summary>Desaloja hasta <paramref name="cuantas"/> claves sin usuarios, primero las más próximas a vencer (bajo <see cref="_candado"/>).</summary>
    private void Desalojar(int cuantas)
    {
        foreach (var (otra, vieja) in _entradas.Where(e => e.Value.Usuarios == 0).OrderBy(e => e.Value.Expira).Take(cuantas).ToList())
        {
            _entradas.Remove(otra);
            vieja.Semaforo.Dispose();
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
