using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Identidad;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Identidad;

/// <summary>
/// HU-037.3 (#211): un <see cref="IClsRegionalUsuario"/> simulado que aplica la regla REAL del alcance por rol
/// (<see cref="clsRegionalUsuarioService.ComponerAlcanceAsync"/> y <see cref="clsRegionalUsuarioService.ComponerPermisoAsync"/>) sobre sus
/// propias respuestas de <c>ResolverAsync</c>, <c>ListarMiembrosAsync</c> y <c>EsMiembroDeMiRegionalAsync</c>. Así las pruebas de los
/// consumidores (tablero, listado regional, detalle y PDF) siguen simulando la regional sin copiar la regla.
/// </summary>
internal static class clsRegionalSustituta
{
    public static IClsRegionalUsuario Crear(IClsUsuarioWebRepository usuarios)
    {
        var regionales = Substitute.For<IClsRegionalUsuario>();
        regionales.ResolverAlcanceAsync(Arg.Any<clsUsuarioWeb>(), Arg.Any<RolInterno>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => clsRegionalUsuarioService.ComponerAlcanceAsync(
                regionales, usuarios, c.ArgAt<clsUsuarioWeb>(0), c.ArgAt<RolInterno>(1), c.ArgAt<string?>(2), c.ArgAt<CancellationToken>(3)));
        regionales.PuedeVerDeAsync(Arg.Any<clsUsuarioWeb>(), Arg.Any<RolInterno>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(c => clsRegionalUsuarioService.ComponerPermisoAsync(
                regionales, c.ArgAt<clsUsuarioWeb>(0), c.ArgAt<RolInterno>(1), c.ArgAt<Guid>(2), c.ArgAt<CancellationToken>(3)));
        return regionales;
    }
}
