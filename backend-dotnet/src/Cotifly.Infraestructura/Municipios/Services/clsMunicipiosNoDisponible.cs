using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Municipios.Services;

/// <summary>
/// Catálogo de municipios cuando Nexo no está configurado (<c>Nexo:UrlBase</c> vacía): responde siempre
/// <c>MUNICIPIOS_NO_DISPONIBLE</c>. El formulario de captura queda sin catálogo de origen/destino. Elegido por
/// <c>clsFabricaMunicipios</c> cuando falta configuración; un adaptador registrado antes (pruebas) se respeta igual.
/// </summary>
public sealed class clsMunicipiosNoDisponible : IClsMunicipiosProvider
{
    public const string Mensaje = "El catálogo de municipios no está disponible (Nexo sin configurar).";

    public Task<clsResultado<IReadOnlyList<clsMunicipio>>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<IReadOnlyList<clsMunicipio>>>(new clsErrorDominio(clsCodigosError.MunicipiosNoDisponible, Mensaje));
}
