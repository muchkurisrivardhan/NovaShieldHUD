using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Beacon.UI.Pet;

/// <summary>The shapes Nova is made of, as WPF meshes: ellipsoids, tori and cylinders.</summary>
public static class MeshKit
{
    /// <summary>An ellipsoid (a sphere when the radii are equal), smooth-shaded.</summary>
    public static MeshGeometry3D Ellipsoid(Point3D c, double rx, double ry, double rz, int slices = 32, int stacks = 20)
    {
        var mesh = new MeshGeometry3D();
        for (var i = 0; i <= stacks; i++)
        {
            var phi = Math.PI * i / stacks;           // 0 at the top
            for (var j = 0; j <= slices; j++)
            {
                var theta = 2 * Math.PI * j / slices;
                var n = new Vector3D(Math.Sin(phi) * Math.Sin(theta), Math.Cos(phi), Math.Sin(phi) * Math.Cos(theta));
                mesh.Positions.Add(new Point3D(c.X + rx * n.X, c.Y + ry * n.Y, c.Z + rz * n.Z));
                // The normal of an ellipsoid: the sphere's, divided by the radii.
                var en = new Vector3D(n.X / rx, n.Y / ry, n.Z / rz);
                en.Normalize();
                mesh.Normals.Add(en);
            }
        }
        for (var i = 0; i < stacks; i++)
            for (var j = 0; j < slices; j++)
            {
                var a = i * (slices + 1) + j;
                var b = a + slices + 1;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(a + 1);
                mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(b + 1);
            }
        mesh.Freeze();
        return mesh;
    }

    /// <summary>A ring lying flat (in the XZ plane) around the Y axis.</summary>
    public static MeshGeometry3D Torus(Point3D c, double major, double minor, int segments = 48, int sides = 10)
    {
        var mesh = new MeshGeometry3D();
        for (var i = 0; i <= segments; i++)
        {
            var u = 2 * Math.PI * i / segments;
            var ring = new Vector3D(Math.Cos(u), 0, Math.Sin(u));
            for (var j = 0; j <= sides; j++)
            {
                var v = 2 * Math.PI * j / sides;
                var n = ring * Math.Cos(v) + new Vector3D(0, Math.Sin(v), 0);
                mesh.Positions.Add(c + ring * major + n * minor);
                mesh.Normals.Add(n);
            }
        }
        for (var i = 0; i < segments; i++)
            for (var j = 0; j < sides; j++)
            {
                var a = i * (sides + 1) + j;
                var b = a + sides + 1;
                mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b);
                mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(b + 1); mesh.TriangleIndices.Add(b);
            }
        mesh.Freeze();
        return mesh;
    }

    /// <summary>An upright cylinder from <paramref name="bottom"/>, open at the ends.</summary>
    public static MeshGeometry3D Cylinder(Point3D bottom, double radius, double height, int sides = 16)
    {
        var mesh = new MeshGeometry3D();
        for (var i = 0; i <= sides; i++)
        {
            var a = 2 * Math.PI * i / sides;
            var n = new Vector3D(Math.Sin(a), 0, Math.Cos(a));
            mesh.Positions.Add(bottom + n * radius);
            mesh.Positions.Add(bottom + n * radius + new Vector3D(0, height, 0));
            mesh.Normals.Add(n); mesh.Normals.Add(n);
        }
        for (var i = 0; i < sides; i++)
        {
            var a = i * 2;
            mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(a + 2); mesh.TriangleIndices.Add(a + 1);
            mesh.TriangleIndices.Add(a + 1); mesh.TriangleIndices.Add(a + 2); mesh.TriangleIndices.Add(a + 3);
        }
        mesh.Freeze();
        return mesh;
    }

    /// <summary>Matte, slightly shiny metal.</summary>
    public static Material Metal(Color color, double shine = 40)
    {
        var m = new MaterialGroup();
        m.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        m.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(0x9A, 0x9E, 0xA6)), shine));
        return m;
    }

    /// <summary>Something that glows: dark underneath, lit by its own colour.</summary>
    public static (Material Material, SolidColorBrush Glow) Glow(Color color)
    {
        var brush = new SolidColorBrush(color);
        var m = new MaterialGroup();
        m.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12))));
        m.Children.Add(new EmissiveMaterial(brush));
        return (m, brush);
    }
}
